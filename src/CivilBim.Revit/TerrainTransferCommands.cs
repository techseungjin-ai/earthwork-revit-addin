using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CivilBim.Core;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.OverlayNG;
using NetTopologySuite.Operation.Overlay;
using Geometry=NetTopologySuite.Geometries.Geometry;

namespace CivilBim.Revit;

public static class TerrainTransfer
{
    // Remain unavailable in normal UI builds until the revised native route is verified.
    public static bool NativeVerified=>false;
    private static void Trace(string value){var folder=Environment.GetEnvironmentVariable("CIVILBIM_REV05_CHECK");if(!string.IsNullOrEmpty(folder))System.IO.File.AppendAllText(System.IO.Path.Combine(folder,"terrain-trace.txt"),value+Environment.NewLine);}
    public sealed class Filter:ISelectionFilter
    {public bool AllowElement(Element e)=>e is Toposolid t&&t.HostTopoId==ElementId.InvalidElementId;public bool AllowReference(Reference r,XYZ p)=>false;}
    public static Geometry Footprint(TerrainMesh mesh)=>OverlayNGRobust.Union(mesh.Triangles.Select(t=>(Geometry)t.Polygon).ToList());
    private static string Key(Point3 p)=>FormattableString.Invariant($"{Math.Round(p.X,6):R},{Math.Round(p.Y,6):R}");
    private static Geometry Intersect(Geometry a,Geometry b)=>OverlayNGRobust.Overlay(a,b,SpatialFunction.Intersection);
    private static void Editable(Toposolid topo)
    {
        if(topo.Pinned||topo.GroupId!=ElementId.InvalidElementId||topo.HostTopoId!=ElementId.InvalidElementId)throw new InvalidDataException("고정·그룹·하위 구획 지형은 처리할 수 없습니다. 원본은 유지됩니다.");
        if(topo.GetSubDivisionIds().Count>0)throw new InvalidDataException("하위 구획이 있는 지형은 먼저 구획을 별도 정리하세요. 원본은 유지됩니다.");
    }
    public static (Toposolid Terrain,int Points) Copy(Toposolid source,Toposolid target)
    {
        if(source.Id==target.Id)throw new InvalidDataException("기준 지형과 대상 지형은 달라야 합니다.");Editable(target);
        var mesh=ContextBuilder.Surface(source);var scope=Footprint(ContextBuilder.Surface(target));
        var doc=target.Document;using var group=new TransactionGroup(doc,"지형 표면 복사 및 검증");group.Start();Toposolid copy;
        using(var t=new Transaction(doc,"기준 고도로 새 지형 작성")){t.Start();Trace("CreateTarget begin");copy=CreateTarget(target,mesh,scope);if(t.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("지형 작성을 취소했습니다.");}Trace("CreateTarget committed");
        copy=TrimCreated(copy,scope);var count=Apply(copy,mesh,scope);Trace("Apply done");using(var t=new Transaction(doc,"검증된 대상 교체")){t.Start();doc.Delete(target.Id);SiteEditing.Stale(doc);t.Commit();}group.Assimilate();return(copy,count);
    }
    private static Toposolid CreateTarget(Toposolid template,TerrainMesh mesh,Geometry scope)
    {
        var doc=template.Document;var surfacePoints=new Dictionary<string,Point3>();
        foreach(var triangle in mesh.In(scope.EnvelopeInternal))foreach(var c in Intersect(triangle.Polygon,scope).Coordinates)
        {var p=new Point3(c.X,c.Y,triangle.Elevation(c.X,c.Y)??throw new InvalidDataException("경계의 표고를 읽지 못했습니다."));if(surfacePoints.TryGetValue(Key(p),out var prior)&&Math.Abs(prior.Z-p.Z)>.001)throw new InvalidDataException("병합 경계의 동일 위치 고도가 다릅니다.");surfacePoints[Key(p)]=p;}
        // Avoid the profiles+points overload which crashed in the local native regression.
        // Create from points first, commit, and trim through Revit's own Split operation.
        Trace("Create from points "+surfacePoints.Count);
        var result=Toposolid.Create(doc,surfacePoints.Values.Select(ModelBuilder.Xyz).ToList(),template.GetTypeId(),template.LevelId);
        return result;
    }
    private static Toposolid TrimCreated(Toposolid terrain,Geometry scope)
    {
        var doc=terrain.Document;var footprint=Footprint(ContextBuilder.Surface(terrain));
        if(OverlayNGRobust.Overlay(footprint,scope,SpatialFunction.SymDifference).Area<Math.Max(1e-6,scope.Area*1e-9))return terrain;
        var polygons=Polygons.Enumerate(scope).ToList();if(polygons.Count!=1)throw new InvalidDataException("여러 개로 분리된 경계는 지원하지 않습니다.");
        var sketch=doc.GetElement(terrain.SketchId) as Sketch??throw new InvalidDataException("지형 분할 스케치 평면을 읽지 못했습니다.");
        var z=sketch.SketchPlane.GetPlane().Origin.Z*.3048;IList<ElementId> ids;
        using(var t=new Transaction(doc,"원래 경계로 지형 분할")){t.Start();ids=terrain.Split(ContextBuilder.Loops(polygons[0],z,doc.Application.ShortCurveTolerance));if(t.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("경계 분할을 취소했습니다.");}
        var matching=ids.Select(id=>(Toposolid)doc.GetElement(id)).Where(p=>OverlayNGRobust.Overlay(Footprint(ContextBuilder.Surface(p)),scope,SpatialFunction.SymDifference).Area<Math.Max(1e-5,scope.Area*1e-8)).ToList();
        if(matching.Count!=1)throw new InvalidDataException("원래 대상 경계와 같은 분할 영역을 확인하지 못했습니다.");
        var chosen=matching[0];using(var t=new Transaction(doc,"지형 외부 조각 정리")){t.Start();var discard=ids.Where(id=>id!=chosen.Id).ToList();if(discard.Count>0)doc.Delete(discard);t.Commit();}return chosen;
    }    private static int Apply(Toposolid target,TerrainMesh mesh,Geometry scope)
    {
        var doc=target.Document;var coverage=Footprint(mesh);var missing=OverlayNGRobust.Overlay(scope,coverage,SpatialFunction.Difference).Area;
        if(missing>Math.Max(1e-6,scope.Area*1e-9))throw new InvalidDataException($"대상 지형 중 {missing:F3}m²가 기준 표면 밖입니다. 외삽하지 않고 원본을 유지합니다.");
        var points=new Dictionary<string,Point3>();var edges=new Dictionary<string,(Point3 A,Point3 B)>();
        void Add(Point3 p){var key=Key(p);if(points.TryGetValue(key,out var previous)&&Math.Abs(previous.Z-p.Z)>.001)throw new InvalidDataException($"같은 위치의 고도가 다릅니다: X {p.X:F3}, Y {p.Y:F3}m. 원본을 유지합니다.");points[key]=p;}
        foreach(var triangle in mesh.In(scope.EnvelopeInternal))
        foreach(var polygon in Polygons.Enumerate(Intersect(triangle.Polygon,scope)))
        foreach(var ring in new[]{polygon.ExteriorRing}.Concat(polygon.InteriorRings))
        {
            var vertices=ring.Coordinates.Select(c=>new Point3(c.X,c.Y,triangle.Elevation(c.X,c.Y)??throw new InvalidDataException("원본 표면의 경계 높이를 읽지 못했습니다."))).ToList();
            foreach(var p in vertices)Add(p);
            for(int i=1;i<vertices.Count;i++){var a=vertices[i-1];var b=vertices[i];if(a.DistanceXY(b)<1e-7)continue;var ka=Key(a);var kb=Key(b);edges[string.CompareOrdinal(ka,kb)<0?ka+"/"+kb:kb+"/"+ka]=(a,b);}
        }
        Trace("Apply points "+points.Count+" edges "+edges.Count);var editor=target.GetSlabShapeEditor();if(!editor.IsEnabled){using var enable=new Transaction(doc,"지형 편집 활성화");enable.Start();editor.Enable();enable.Commit();}
        Trace("Native constructor points applied");
        var current=ContextBuilder.Surface(target);
        foreach(var edge in edges.Values)
        {
            if(edge.A.DistanceXY(edge.B)<doc.Application.ShortCurveTolerance*.3048)continue;
            if(new[]{.25,.5,.75}.All(t=>{var x=edge.A.X+(edge.B.X-edge.A.X)*t;var y=edge.A.Y+(edge.B.Y-edge.A.Y)*t;var expected=edge.A.Z+(edge.B.Z-edge.A.Z)*t;var actual=current.NearElevation(x,y,.00001);return actual.HasValue&&Math.Abs(actual.Value-expected)<.0001;}))continue;
            var ka=Key(edge.A);var kb=Key(edge.B);
            var lookup=editor.SlabShapeVertices.Cast<SlabShapeVertex>().ToDictionary(v=>Key(ModelBuilder.Meters(v.Position)));
            if(!lookup.TryGetValue(Key(edge.A),out var a)||!lookup.TryGetValue(Key(edge.B),out var b))throw new InvalidDataException("표면 경계점을 Revit에 생성하지 못했습니다. 원본을 유지합니다.");
            Trace("AddSplitLine "+ka+" to "+kb);using(var edit=new Transaction(doc,"원본 표면 분할선 반영")){edit.Start();editor.AddSplitLine(a,b);if(edit.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("분할선 반영을 취소했습니다.");}current=ContextBuilder.Surface(target);
        }
        Validate(mesh,ContextBuilder.Surface(target),scope);return points.Count;
    }
    public static double Validate(TerrainMesh source,TerrainMesh actual,Geometry scope)
    {
        if(OverlayNGRobust.Overlay(scope,Footprint(actual),SpatialFunction.SymDifference).Area>Math.Max(1e-5,scope.Area*1e-8))throw new InvalidDataException("작성 표면의 경계 면적이 다릅니다. 원본을 유지합니다.");
        double maximum=0;
        foreach(var a in actual.Triangles)foreach(var b in source.In(a.Polygon.EnvelopeInternal))
        foreach(var polygon in Polygons.Enumerate(Intersect(a.Polygon,b.Polygon)))
        foreach(var p in polygon.Coordinates)
        {var za=a.Elevation(p.X,p.Y);var zb=b.Elevation(p.X,p.Y);if(za.HasValue&&zb.HasValue)maximum=Math.Max(maximum,Math.Abs(za.Value-zb.Value));}
        if(maximum>.001)throw new InvalidDataException($"표면 높이 검증 오차 {maximum*1000:F2}mm입니다. 원본을 유지합니다.");return maximum;
    }
    public static Toposolid Merge(Document doc,IReadOnlyList<Toposolid> terrains)
    {
        if(terrains.Count<2)throw new InvalidDataException("병합할 지형을 두 개 이상 선택하세요.");foreach(var t in terrains)Editable(t);var first=terrains[0];
        if(terrains.Any(t=>t.GetTypeId()!=first.GetTypeId()||t.LevelId!=first.LevelId||t.CreatedPhaseId!=first.CreatedPhaseId))throw new InvalidDataException("같은 유형·레벨·생성 단계의 지형끼리 병합하세요.");
        if(terrains.Any(t=>JoinGeometryUtils.GetJoinedElements(doc,t).Count>0))throw new InvalidDataException("다른 요소와 결합된 지형은 결합 해제 후 병합하세요. 원본을 유지합니다.");
        var meshes=terrains.Select(ContextBuilderSurface).ToList();var footprints=meshes.Select(Footprint).ToList();
        for(int i=0;i<footprints.Count;i++)for(int j=i+1;j<footprints.Count;j++)if(Intersect(footprints[i],footprints[j]).Area>1e-6)throw new InvalidDataException("면적이 중복된 지형은 병합할 수 없습니다.");
        var union=OverlayNGRobust.Union(footprints);var polygons=Polygons.Enumerate(union).ToList();if(polygons.Count!=1)throw new InvalidDataException("경계가 맞닿은 지형끼리 선택하세요. 틈은 임의로 메우지 않습니다.");
        using var group=new TransactionGroup(doc,"분할 지형 병합 및 검증");group.Start();var mesh=new TerrainMesh(meshes.SelectMany(m=>m.Triangles));Toposolid result;
        using(var t=new Transaction(doc,"병합 지형 작성")){t.Start();result=CreateTarget(first,mesh,union);if(t.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("병합 지형 작성을 취소했습니다.");}result=TrimCreated(result,union);Apply(result,mesh,union);
        // Validate before deleting originals; callers own an atomic transaction.
        using(var t=new Transaction(doc,"검증된 병합 지형 교체")){t.Start();doc.Delete(terrains.Select(t=>t.Id).ToList());SiteEditing.Stale(doc);t.Commit();}group.Assimilate();return result;
    }
    private static TerrainMesh ContextBuilderSurface(Toposolid t)=>ContextBuilder.Surface(t);
}

[Transaction(TransactionMode.Manual)]
public sealed class TerrainSurfaceCopyCommand:IExternalCommand
{
    public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)
    {
        var ui=data.Application.ActiveUIDocument;if(ui==null)return Result.Cancelled;
        if(!TerrainTransfer.NativeVerified){TaskDialog.Show("지형 표면 복사","실제 Revit 안정성 검증 중으로 현재 사용할 수 없습니다.");return Result.Cancelled;}
        try
        {
            TaskDialog.Show("지형 표면 복사 · 1/2","고도가 맞춰진 기준 지형(그림의 ②)을 선택하세요.");var source=(Toposolid)ui.Document.GetElement(ui.Selection.PickObject(ObjectType.Element,new TerrainTransfer.Filter(),"기준 지형 ② 선택"));
            TaskDialog.Show("지형 표면 복사 · 2/2","표면 고도를 맞출 대상 지형(그림의 ①)을 선택하세요. 경계·유형을 보존한 새 지형으로 교체합니다. 요소 ID 및 기존 호스트 연결은 이전되지 않습니다.");var target=(Toposolid)ui.Document.GetElement(ui.Selection.PickObject(ObjectType.Element,new TerrainTransfer.Filter(),"대상 지형 ① 선택"));
            var result=TerrainTransfer.Copy(source,target);ui.Selection.SetElementIds(new[]{result.Terrain.Id});TaskDialog.Show("표면 복사 완료",$"표면점 {result.Points:N0}개 반영 및 높이 오차 1mm 이내 검증 완료. 수정 후 물량을 다시 계산하세요.");return Result.Succeeded;
        }
        catch(Autodesk.Revit.Exceptions.OperationCanceledException){return Result.Cancelled;}catch(Exception ex){message=ex.Message;return Result.Failed;}
    }
}
[Transaction(TransactionMode.Manual)]
public sealed class TerrainSolidMergeCommand:IExternalCommand
{
    public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)
    {
        var ui=data.Application.ActiveUIDocument;if(ui==null)return Result.Cancelled;
        if(!TerrainTransfer.NativeVerified){TaskDialog.Show("분할 지형 병합","실제 Revit 안정성 검증 중으로 현재 사용할 수 없습니다.");return Result.Cancelled;}
        try
        {
            TaskDialog.Show("분할 지형 병합","SL로 나눈 지형 솔리드를 두 개 이상 선택한 뒤 완료를 누르세요. 같은 유형·레벨·생성 단계이며 서로 맞닿은 지형을 하나로 만듭니다. 원본 경계의 높이를 보존하며, 고도 충돌이나 검증 실패 시 원본을 유지합니다.");
            var selected=ui.Selection.PickObjects(ObjectType.Element,new TerrainTransfer.Filter(),"병합할 지형 선택 후 완료").Select(r=>(Toposolid)ui.Document.GetElement(r)).DistinctBy(t=>t.Id).ToList();
            var merged=TerrainTransfer.Merge(ui.Document,selected);ui.Selection.SetElementIds(new[]{merged.Id});TaskDialog.Show("지형 병합 완료","하위요소로 편집할 수 있는 지형 솔리드 하나로 병합했습니다. 표면 높이 오차 1mm 이내 검증 완료. 수정 후 물량을 다시 계산하세요.");return Result.Succeeded;
        }
        catch(Autodesk.Revit.Exceptions.OperationCanceledException){return Result.Cancelled;}catch(Exception ex){message=ex.Message;return Result.Failed;}
    }
}

public static class TerrainTransferChecks
{
    public static void Run(UIApplication ui,string folder)
    {
        var doc=ui.OpenAndActivateDocument(System.IO.Path.Combine(folder,"프로젝트-복사본.rvt")).Document;
        var type=new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).FirstElementId();var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstElementId();
        Polygon Rect(double x,double y,double w,double h)=>Polygons.Factory.CreatePolygon(new[]{new Coordinate(x,y),new(x+w,y),new(x+w,y+h),new(x,y+h),new(x,y)});
        Toposolid source,target;TerrainMesh original;int points;double error;
        using(var t=new Transaction(doc,"표면 복사 검증 입력"))
        {
            t.Start();source=Toposolid.Create(doc,new[]{new Point3(1000,1000,10),new(1020,1000,12),new(1020,1020,16),new(1000,1020,11),new(1008,1007,17),new(1014,1013,13)}.Select(ModelBuilder.Xyz).ToList(),type,level);
            target=Toposolid.Create(doc,ContextBuilder.Loops(Rect(1002.3,1003.1,13,12),0,doc.Application.ShortCurveTolerance),type,level);doc.Regenerate();original=ContextBuilder.Surface(source);t.Commit();
        }
        var copy=TerrainTransfer.Copy(source,target);target=copy.Terrain;points=copy.Points;error=TerrainTransfer.Validate(original,ContextBuilder.Surface(target),TerrainTransfer.Footprint(ContextBuilder.Surface(target)));
        List<Toposolid> parts;using(var t=new Transaction(doc,"SL 분할 검증")){t.Start();var split=source.Split(ContextBuilder.Loops(Rect(1000,1000,10,20),0,doc.Application.ShortCurveTolerance));doc.Regenerate();parts=split.Select(id=>(Toposolid)doc.GetElement(id)).ToList();t.Commit();}
        var merged=TerrainTransfer.Merge(doc,parts);TerrainTransfer.Validate(original,ContextBuilder.Surface(merged),TerrainTransfer.Footprint(original));
        System.IO.File.WriteAllText(System.IO.Path.Combine(folder,"check.json"),System.Text.Json.JsonSerializer.Serialize(new{Status="passed",CopyPoints=points,CopyMaximumErrorMm=error*1000,SplitParts=parts.Count,MergedId=merged.Id.Value},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        doc.SaveAs(System.IO.Path.Combine(folder,"지형복사-병합-검증.rvt"),new SaveAsOptions{OverwriteExistingFile=true});
    }
}

