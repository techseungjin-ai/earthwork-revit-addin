using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CivilBim.Core;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CivilBim.Revit;

public static class SiteEditing
{
    public static Toposolid Terrain(UIDocument ui)
    {
        TaskDialog.Show("CivilBIM · 1단계","수정할 원본 지형 솔리드를 선택하세요. 도로 하위 구획은 선택하지 않습니다. 이 안내를 닫으면 선택을 시작합니다.");
        return ui.Document.GetElement(ui.Selection.PickObject(ObjectType.Element,new TerrainFilter(),"수정할 원본 지형을 선택하세요.")) as Toposolid??throw new InvalidDataException("원본 지형이 필요합니다.");
    }
    private sealed class TerrainFilter:ISelectionFilter
    {
        public bool AllowElement(Element e)=>e is Toposolid t&&t.HostTopoId==ElementId.InvalidElementId;
        public bool AllowReference(Reference r,XYZ p)=>false;
    }
    public static void Stale(Document doc)
    {
        foreach(var (e,info) in Storage.Generated(doc).Where(x=>x.Info.Kind=="SiteSoil"))
        {
            QuantityParameters.MarkStale(e);
            OverlapParameters.MarkStale(e);
            Storage.Write(e,info with {Source="WORKSITE:지형 수정 후 재계산 필요"});
        }
    }
    public static int Move(Toposolid terrain,XYZ min,XYZ max,XYZ delta,XYZ? right=null,XYZ? up=null)
    {
        var doc=terrain.Document;var editor=terrain.GetSlabShapeEditor();if(!editor.IsEnabled)editor.Enable();doc.Regenerate();
        right??=XYZ.BasisX;up??=XYZ.BasisY;var x0=Math.Min(min.DotProduct(right),max.DotProduct(right));var x1=Math.Max(min.DotProduct(right),max.DotProduct(right));var y0=Math.Min(min.DotProduct(up),max.DotProduct(up));var y1=Math.Max(min.DotProduct(up),max.DotProduct(up));
        var vertices=editor.SlabShapeVertices.Cast<SlabShapeVertex>().Where(v=>v.Position.DotProduct(right)>=x0&&v.Position.DotProduct(right)<=x1&&v.Position.DotProduct(up)>=y0&&v.Position.DotProduct(up)<=y1).ToList();
        if(vertices.Count==0)throw new InvalidDataException($"선택한 원본 지형의 정점 {editor.SlabShapeVertices.Size:N0}개 중 지정 영역에 포함된 점이 없습니다. 다른 지형/도로를 선택하지 않았는지와 실제 지형점 위치를 확인하세요.");
        var points=vertices.Select(v=>v.Position+new XYZ(delta.X,delta.Y,0)).ToList();
        // Revit refuses corner deletion. Roll back the complete edit rather than moving only part of the selection.
        foreach(var v in vertices)if(!editor.DeletePoint(v))throw new InvalidDataException("경계 꼭짓점은 이동할 수 없습니다. 지형 외곽을 제외하고 영역을 지정하세요.");
        editor.AddPoints(points);doc.Regenerate();Stale(doc);return points.Count;
    }
    public static int MapElevations(Toposolid terrain,IEnumerable<TextNote> notes,double elevationOrigin)
    {
        var editor=terrain.GetSlabShapeEditor();if(!editor.IsEnabled)editor.Enable();terrain.Document.Regenerate();
        var parsed=new List<XYZ>();
        foreach(var note in notes)
        {
            var value=Regex.Match(note.Text.Trim(),@"^(?:(?:EL|E\.L\.?|표고)\s*[:=]?\s*)?([+-]?\d+(?:\.\d+)?)\s*(?:m)?$",RegexOptions.IgnoreCase);
            if(!value.Success)throw new InvalidDataException($"표고 문자 '{note.Text.Trim()}'를 해석할 수 없습니다. 숫자 또는 EL +숫자 형식을 선택하세요.");
            var z=double.Parse(value.Groups[1].Value,CultureInfo.InvariantCulture)-elevationOrigin;
            parsed.Add(new(note.Coord.X,note.Coord.Y,ModelBuilder.Feet(z)));
        }
        if(parsed.Count==0)throw new InvalidDataException("Revit 표고 문자(TextNote)를 먼저 선택하세요. CAD 내부 문자는 Revit 문자로 변환 후 선택하세요.");
        var groups=parsed.GroupBy(p=>(Math.Round(p.X,6),Math.Round(p.Y,6))).ToList();
        if(groups.Any(g=>g.Max(p=>p.Z)-g.Min(p=>p.Z)>ModelBuilder.Feet(.001)))throw new InvalidDataException("같은 위치의 표고값이 서로 다릅니다.");
        foreach(var p in groups.Select(g=>g.First()))
        {
            var vertex=editor.SlabShapeVertices.Cast<SlabShapeVertex>().FirstOrDefault(v=>Math.Abs(v.Position.X-p.X)<1e-6&&Math.Abs(v.Position.Y-p.Y)<1e-6);
            if(vertex!=null&&!editor.DeletePoint(vertex))throw new InvalidDataException("표고 문자가 지형 외곽 꼭짓점과 겹칩니다. 내부 측량점을 선택하세요.");
            editor.AddPoint(p);
        }
        terrain.Document.Regenerate();Stale(terrain.Document);return groups.Count;
    }
    public static List<ElementId> Merge(Document doc,IReadOnlyList<Toposolid> roads)
    {
        if(roads.Count<2||roads.Any(r=>r.HostTopoId==ElementId.InvalidElementId))throw new InvalidDataException("합칠 도로 하위 구획을 두 개 이상 선택하세요.");
        var first=roads[0];var offset=first.get_Parameter(BuiltInParameter.TOPOSOLID_SUBDIVIDE_HEIGHT).AsDouble();
        if(roads.Any(r=>r.HostTopoId!=first.HostTopoId||r.GetTypeId()!=first.GetTypeId()||Math.Abs(r.get_Parameter(BuiltInParameter.TOPOSOLID_SUBDIVIDE_HEIGHT).AsDouble()-offset)>1e-7))throw new InvalidDataException("같은 원본 지형·유형·높이 오프셋의 도로만 합칠 수 있습니다.");
        var polygons=new List<NetTopologySuite.Geometries.Geometry>();
        foreach(var road in roads)
        {
            var sketch=doc.GetElement(road.SketchId) as Sketch??throw new InvalidDataException("도로 구획 스케치를 읽을 수 없습니다.");
            NetTopologySuite.Geometries.Geometry? footprint=null;
            foreach(CurveArray ring in sketch.Profile)
            {
                var curves=ring.Cast<Curve>().ToList();var ordered=new List<XYZ>();
                while(curves.Count>0)
                {
                    var index=ordered.Count==0?0:curves.FindIndex(c=>c.GetEndPoint(0).DistanceTo(ordered[^1])<1e-5||c.GetEndPoint(1).DistanceTo(ordered[^1])<1e-5);
                    if(index<0)throw new InvalidDataException("도로 스케치 선이 이어져 있지 않습니다. 원본 도로를 유지합니다.");
                    var curve=curves[index];curves.RemoveAt(index);var pts=curve.Tessellate().ToList();if(ordered.Count>0&&pts[0].DistanceTo(ordered[^1])>1e-5)pts.Reverse();if(ordered.Count==0)ordered.Add(pts[0]);ordered.AddRange(pts.Skip(1));
                }
                if(ordered[0].DistanceTo(ordered[^1])>1e-5)throw new InvalidDataException("도로 경계가 폐합되지 않았습니다.");
                var coords=ordered.SkipLast(1).Select(p=>new Coordinate(p.X*.3048,p.Y*.3048)).ToList();
                if(coords.Count<3)throw new InvalidDataException("유효하지 않은 도로 경계입니다.");coords.Add(coords[0].Copy());
                var p=Polygons.Factory.CreatePolygon(coords.ToArray());footprint=footprint==null?p:footprint.SymmetricDifference(p);
            }
            polygons.Add(footprint??throw new InvalidDataException("빈 도로 경계입니다."));
        }
        var union=RoadBoundary.Merge(polygons,doc.Application.ShortCurveTolerance*.3048);var host=(Toposolid)doc.GetElement(first.HostTopoId);var type=first.GetTypeId();var info=Storage.Info(first);
        doc.Delete(roads.Select(r=>r.Id).ToList());doc.Regenerate();var result=new List<ElementId>();
        foreach(var polygon in Polygons.Enumerate(union))
        {
            Toposolid road;
            try{road=host.CreateSubDivision(doc,type,ContextBuilder.Loops(polygon,0,doc.Application.ShortCurveTolerance));}
            catch(Autodesk.Revit.Exceptions.ArgumentException ex){throw new InvalidDataException($"Revit이 합친 도로 경계를 거부했습니다(면적{polygon.Area:F3}m², 구멍{polygon.NumInteriorRings}개). 접점/최소선분 또는 스케치 평면을 추가 확인해야 합니다. 원본 도로는 되돌립니다.",ex);}
            road.get_Parameter(BuiltInParameter.TOPOSOLID_SUBDIVIDE_HEIGHT).Set(offset);
            if(info!=null)Storage.Write(road,info with {ParentId="MERGED-"+road.Id.Value});result.Add(road.Id);
        }
        if(result.Count==0)throw new InvalidDataException("합칠 도로 경계가 비었습니다. 원본을 유지합니다.");return result;
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class RoadMergeCommand:IExternalCommand
{
    public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)
    {
        var ui=data.Application.ActiveUIDocument;if(ui==null)return Result.Cancelled;
        try{TaskDialog.Show("CivilBIM · 도로 합치기","합칠 도로 하위 구획을 두 개 이상 선택한 뒤 완료를 누르세요. 같은 원본 지형·유형·높이끼리 병합합니다. 이 안내를 닫으면 선택을 시작합니다.");var roads=ui.Selection.PickObjects(ObjectType.Element,new RoadFilter(),"도로 구획들을 선택 후 완료").Select(r=>(Toposolid)ui.Document.GetElement(r)).DistinctBy(r=>r.Id).ToList();using var t=new Transaction(ui.Document,"도로 구획 합치기");t.Start();var ids=SiteEditing.Merge(ui.Document,roads);if(t.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("도로 병합이 취소되었습니다. 원본이 유지됩니다.");ui.Selection.SetElementIds(ids);return Result.Succeeded;}
        catch(Autodesk.Revit.Exceptions.OperationCanceledException){return Result.Cancelled;}
        catch(Exception ex){message=ex.Message;return Result.Failed;}
    }
    private sealed class RoadFilter:ISelectionFilter
    {public bool AllowElement(Element e)=>e is Toposolid t&&t.HostTopoId!=ElementId.InvalidElementId;public bool AllowReference(Reference r,XYZ p)=>false;}
}
[Transaction(TransactionMode.Manual)]
public sealed class TerrainMoveCommand:IExternalCommand
{
    public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)
    {
        var ui=data.Application.ActiveUIDocument;if(ui==null)return Result.Cancelled;
        try
        {
            if(Math.Abs(ui.ActiveView.ViewDirection.Z)<.999)throw new InvalidDataException("평면 뷰에서 실행하세요.");
            var terrain=SiteEditing.Terrain(ui);TaskDialog.Show("CivilBIM · 2단계","이동할 지형점의 영역을 드래그하세요. 지형 외곽 꼭짓점은 제외합니다. 드래그 방향은 어느 쪽이든 가능합니다.");var box=ui.Selection.PickBox(PickBoxStyle.Directional,"이동할 지형점 영역을 지정하세요.");
            TaskDialog.Show("CivilBIM · 3단계","이동 기준점 → 도착점을 순서대로 클릭하세요. 점의 표고는 유지하고 평면 위치만 이동합니다.");
            var source=ui.Selection.PickPoint("이동 기준점");var target=ui.Selection.PickPoint("이동 도착점");
            using var t=new Transaction(ui.Document,"지형점 일괄 XY 이동");t.Start();var count=SiteEditing.Move(terrain,box.Min,box.Max,target-source,ui.ActiveView.RightDirection,ui.ActiveView.UpDirection);t.Commit();TaskDialog.Show("CivilBIM",$"지형점 {count}개를 이동했습니다. 작업대지 지층이 있으면 굴착 물량을 다시 생성하세요.");return Result.Succeeded;
        }
        catch(Autodesk.Revit.Exceptions.OperationCanceledException){return Result.Cancelled;}
        catch(Exception ex){message=ex.Message;return Result.Failed;}
    }
}
[Transaction(TransactionMode.Manual)]
public sealed class TerrainElevationCommand:IExternalCommand
{
    public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)
    {
        var ui=data.Application.ActiveUIDocument;if(ui==null)return Result.Cancelled;
        try
        {
            var terrain=SiteEditing.Terrain(ui);TaskDialog.Show("CivilBIM · 2단계","표고값이 적힌 Revit 문자들을 선택하고 완료를 누르세요. CAD 내부 문자는 먼저 Revit 문자로 변환해야 합니다.");var notes=ui.Selection.PickObjects(ObjectType.Element,new NoteFilter(),"표고 문자 선택 후 완료").Select(r=>(TextNote)ui.Document.GetElement(r)).ToList();
            using var t=new Transaction(ui.Document,"현황측량 문자 표고 반영");t.Start();var count=SiteEditing.MapElevations(terrain,notes,Storage.Load(ui.Document).Frame.Elevation);t.Commit();TaskDialog.Show("CivilBIM",$"측량 표고 {count}개를 반영했습니다. 작업대지 지층이 있으면 굴착 물량을 다시 생성하세요.");return Result.Succeeded;
        }
        catch(Autodesk.Revit.Exceptions.OperationCanceledException){return Result.Cancelled;}
        catch(Exception ex){message=ex.Message;return Result.Failed;}
    }
    private sealed class NoteFilter:ISelectionFilter
    {public bool AllowElement(Element e)=>e is TextNote;public bool AllowReference(Reference r,XYZ p)=>false;}
}
