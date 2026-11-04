using Autodesk.Revit.DB;
using CivilBim.Core;
using NetTopologySuite.Geometries;
using NtsGeometry=NetTopologySuite.Geometries.Geometry;

namespace CivilBim.Revit;

public sealed record ContextResult(Toposolid Terrain,List<ElementId> Roads,List<ElementId> Buildings,View3D View,SiteDataset Dataset,TerrainMesh Surface,List<string> Log);
public static class ContextBuilder
{
    public static ContextResult Generate(Document doc,Settings s)
    {
        Guard.Finite(s.TerrainReductionPercent);if(s.TerrainReductionPercent<0||s.TerrainReductionPercent>=100)throw new InvalidDataException("지형점 감소율 범위는 0 이상100 미만입니다.");
        var previous=Storage.Generated(doc);
        if(previous.Any(x=>x.Info.Kind is "Terrain" or "Road" or "Building"))throw new InvalidDataException("이미 CivilBIM 대지가 있습니다. 회전값만 바꿔 기존 대지를 회전하지는 않습니다. 새 프로젝트에서 실행하거나 기존 생성 대지·도로·건물을 먼저 정리하세요.");
        var data=SiteDataset.Load(s.SiteFolder,s.Scale,s.Sampling,s.BuildingFloorHeight,rotationDegrees:s.Frame.RotationDegrees,rotationPivot:s.SiteRotationPivot);s.Frame=data.Frame;
        var log=data.Log;var roads=new List<ElementId>();var buildings=new List<ElementId>();
        var terrainType=doc.GetElement(s.TerrainType) as ToposolidType??throw new InvalidDataException("선택한 지형 유형이 없습니다.");
        var roadType=doc.GetElement(s.RoadType) as ToposolidType??throw new InvalidDataException("선택한 도로 유형이 없습니다.");
        var material=doc.GetElement(s.BuildingMaterial) as Material??throw new InvalidDataException("선택한 건물 재질이 없습니다.");
        var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l=>Math.Abs(l.Elevation)).FirstOrDefault()??throw new InvalidDataException("레벨이 필요합니다.");
        using var group=new TransactionGroup(doc,"CivilBIM SHP 대지 작성");group.Start();Toposolid terrain;TerrainMesh surface;
        var batch=Guid.NewGuid().ToString();
        using(var t=new Transaction(doc,"SHP 지형"))
        {
            t.Start();t.SetFailureHandlingOptions(t.GetFailureHandlingOptions().SetFailuresPreprocessor(new ContextFailures(log)).SetClearAfterRollback(true));terrain=Toposolid.Create(doc,data.Points.Select(ModelBuilder.Xyz).ToList(),terrainType.Id,level.Id);doc.Regenerate();
            var editor=terrain.GetSlabShapeEditor();if(!editor.IsEnabled)editor.Enable();doc.Regenerate();var createdCount=editor.SlabShapeVertices.Size;
            log.Add($"지형 생성 요청 {data.Points.Count:N0}점 / Revit 생성 정점 {createdCount:N0}개.");
            if(createdCount<data.Points.Count)throw new InvalidDataException($"Revit에서 입력점 {data.Points.Count:N0}개 중 정점 {createdCount:N0}개만 생성했습니다. 원본 점 유지 조건을 만족하지 않아 되돌립니다. 실제 Revit 생성 한도/중복 처리를 검토해야 합니다.");
            if(s.TerrainReductionPercent>0)
            {
                var before=terrain.GetSlabShapeEditor().SlabShapeVertices.Size;terrain.Simplify(1-s.TerrainReductionPercent/100);doc.Regenerate();
                var after=terrain.GetSlabShapeEditor().SlabShapeVertices.Size;log.Add($"Revit 지형 솔리드 단순화: 요청 감소율{s.TerrainReductionPercent:F1}% / 실제 정점{before:N0}→{after:N0}. 형상보존을 위한 Revit 판단으로 감소율이 다를 수 있습니다. 등고선/상세 지형을 확인하세요.");
            }
            Storage.Write(terrain,new ElementInfo("Terrain","SITE","지형",data.Points.Max(p=>p.Z),data.Points.Min(p=>p.Z),0,0,0,"",s.SiteFolder,batch,new(),s.Frame,s.Scale));
            Storage.Write(doc.ProjectInformation,s);if(t.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("지형 생성 트랜잭션 실패:\n"+string.Join("\n",log));
        }
        surface=Surface(terrain,log);
        var hull=NetTopologySuite.Operation.Union.UnaryUnionOp.Union(surface.Triangles.Select(t=>(NtsGeometry)t.Polygon).ToList());
        var roadGeometry=data.Roads.Intersection(hull);
        log.Add($"Revit 실제 지형 TIN 범위 {hull.Area:F2}m²를 도로/건물 생성 경계로 사용합니다.");
        using(var t=new Transaction(doc,"SHP 도로 구획 / 주변 건물"))
        {
            t.Start();t.SetFailureHandlingOptions(t.GetFailureHandlingOptions().SetFailuresPreprocessor(new ContextFailures(log)).SetClearAfterRollback(true));
            var roadFailureArea=0.0;
            void CreateRoad(Polygon polygon,int depth)
            {
                using var sub=new SubTransaction(doc);sub.Start();
                try
                {
                    var road=terrain.CreateSubDivision(doc,roadType.Id,Loops(polygon,0,doc.Application.ShortCurveTolerance));
                    road.get_Parameter(BuiltInParameter.TOPOSOLID_SUBDIVIDE_HEIGHT).Set(ModelBuilder.Feet(s.RoadOffset));
                    Storage.Write(road,new ElementInfo("Road",$"ROAD-{roads.Count+1}","도로",0,0,0,0,0,"",s.SiteFolder,batch,new(),s.Frame,s.Scale));if(sub.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("도로 구획 하위 트랜잭션이 취소되었습니다.");roads.Add(road.Id);
                }
                catch(Exception ex)
                {
                    if(sub.GetStatus()==TransactionStatus.Started)sub.RollBack();
                    if(depth<3)
                    {
                        var size=new[]{80.0,20.0,5.0}[depth];
                        log.Add($"복잡 도로망 {polygon.Area:F1}m²를 {size}m 구획으로 분할하여 재시도합니다.");
                        var e=polygon.EnvelopeInternal;
                        for(var x=e.MinX;x<e.MaxX;x+=size)for(var y=e.MinY;y<e.MaxY;y+=size)
                        {
                            var tile=Polygons.Factory.ToGeometry(new Envelope(x,Math.Min(x+size,e.MaxX),y,Math.Min(y+size,e.MaxY)));
                            foreach(var piece in Polygons.Enumerate(polygon.Intersection(tile)))CreateRoad(piece,depth+1);
                        }
                    }
                    else if(depth==3)
                    {
                        foreach(var triangle in Polygons.Enumerate(NetTopologySuite.Triangulate.Polygon.PolygonTriangulator.Triangulate(polygon)))CreateRoad(triangle,4);
                    }
                    else {roadFailureArea+=polygon.Area;log.Add($"도로 면적 {polygon.Area:F1}m² 생성 실패: {ex.Message}");}
                }
            }
            foreach(var polygon in Polygons.Enumerate(roadGeometry))CreateRoad(polygon,0);
            log.Add($"도로 대상 {roadGeometry.Area:F2}m² / 실패 {roadFailureArea:F4}m².");
            if(roadFailureArea>Math.Max(.1,roadGeometry.Area*.001))throw new InvalidDataException("도로 구획의 실패 면적이 0.1%를 초과했습니다. 전체 대지 생성을 취소합니다.\n"+string.Join("\n",log));
            var category=new ElementId(BuiltInCategory.OST_Mass);if(!DirectShape.IsValidCategoryId(category,doc)){category=new(BuiltInCategory.OST_GenericModel);log.Add("이 Revit 템플릿은 Mass DirectShape를 허용하지 않아 건물 매스를 일반 모델 카테고리로 작성합니다.");}
            foreach(var building in data.Buildings)
            {
                foreach(var polygon in Polygons.Enumerate(building.Footprint.Intersection(hull)))
                {
                    using var sub=new SubTransaction(doc);sub.Start();
                    try
                    {
                        var center=polygon.InteriorPoint;var z=surface.NearElevation(center.X,center.Y,.05);
                        if(z==null)
                        {
                            var elevations=polygon.ExteriorRing.Coordinates.Select(p=>surface.NearElevation(p.X,p.Y,.1)).Where(v=>v.HasValue).Select(v=>v!.Value).ToList();
                            if(elevations.Count>0)z=elevations.Average();
                        }
                        if(z==null)
                        {
                            foreach(var triangle in surface.In(polygon.EnvelopeInternal))
                            {
                                var overlap=triangle.Polygon.Intersection(polygon);if(overlap.Area<1e-6)continue;
                                var p=overlap.InteriorPoint;z=triangle.Elevation(p.X,p.Y);if(z.HasValue)break;
                            }
                        }
                        if(z==null)throw new InvalidDataException("건물 중심의 지형 표고를 찾지 못했습니다.");
                        var solid=GeometryCreationUtilities.CreateExtrusionGeometry(Loops(polygon,z.Value,doc.Application.ShortCurveTolerance),XYZ.BasisZ,ModelBuilder.Feet(building.Height),new SolidOptions(material.Id,ElementId.InvalidElementId));
                        var shape=DirectShape.CreateElement(doc,category);shape.Name="주변건물 · "+building.Id;shape.SetShape(new GeometryObject[]{solid});
                        Storage.Write(shape,new ElementInfo("Building",building.Id,"주변건물",z.Value+building.Height,z.Value,0,ModelBuilder.Volume(solid),building.Height,"",s.SiteFolder,batch,new(center.X,center.Y,z.Value),s.Frame,s.Scale));
                        sub.Commit();buildings.Add(shape.Id);
                    }
                    catch(Exception ex){sub.RollBack();log.Add($"건물 {building.Id} 생성 실패: {ex.Message}");}
                }
            }
            if(t.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("도로/건물 Revit 트랜잭션 실패:\n"+string.Join("\n",log));
        }
        View3D view;
        using(var t=new Transaction(doc,"대지 검토 뷰"))
        {
            t.Start();doc.Regenerate();
            var actualRoads=new FilteredElementCollector(doc).OfClass(typeof(Toposolid)).Cast<Toposolid>().Where(r=>r.HostTopoId==terrain.Id).ToList();
            var missingIds=roads.Count(id=>doc.GetElement(id) is not Toposolid);roads=actualRoads.Select(r=>r.Id).ToList();
            var actualPolygons=actualRoads.SelectMany(r=>Surface(r,log).Triangles).Select(t=>(NtsGeometry)t.Polygon).ToList();
            var actualCoverage=actualPolygons.Count==0?Polygons.Factory.CreatePolygon():NetTopologySuite.Operation.Union.UnaryUnionOp.Union(actualPolygons);
            var missingArea=roadGeometry.Difference(actualCoverage).Area;var extraArea=actualCoverage.Difference(roadGeometry).Area;
            log.Add($"Revit 확정 도로 {roads.Count}구획 / 재생성 전 ID 소실 {missingIds} / 실제 미피복 {missingArea:F4}m² / 초과 {extraArea:F4}m².");
            if(missingArea+extraArea>Math.Max(.1,roadGeometry.Area*.001))throw new InvalidDataException("Revit 확정 도로의 실제 피복 면적이 입력과 다릅니다. 대지 전체를 취소합니다.");
            foreach(var road in actualRoads)if(Storage.Info(road)==null)Storage.Write(road,new ElementInfo("Road",$"ROAD-{road.Id.Value}","도로",0,0,0,0,0,"",s.SiteFolder,batch,new(),s.Frame,s.Scale));
            view=Views.Create(doc,"01_대지·도로·주변건물",data.Points);
            foreach(var id in roads)view.SetElementOverrides(id,new OverrideGraphicSettings().SetSurfaceTransparency(0).SetProjectionLineColor(new(65,75,88)));
            StartingViewSettings.GetStartingViewSettings(doc).ViewId=view.Id;t.Commit();
        }
        group.Assimilate();log.Add($"완료: 지형 1개 / 도로 구획 {roads.Count}개 / 주변 건물 {buildings.Count}동.");
        return new(terrain,roads,buildings,view,data,surface,log);
    }
    public static TerrainMesh Surface(Toposolid terrain,List<string>? log=null)
    {
        var triangles=new List<TerrainTriangle>();
        foreach(var solid in FamilyBuilder.Solids(terrain.get_Geometry(new Options {DetailLevel=ViewDetailLevel.Fine})))foreach(Face face in solid.Faces)
        {
            if(face.Area*.3048*.3048<=1e-8)continue;
            var uv=face.GetBoundingBox();var mid=new UV((uv.Min.U+uv.Max.U)/2,(uv.Min.V+uv.Max.V)/2);
            if(face.ComputeNormal(mid).Z<=1e-8)continue;
            var mesh=face.Triangulate()??face.Triangulate(1.0);
            if(mesh==null)
            {
                var area=face.Area*.3048*.3048;
                if(area<=.001){log?.Add($"Revit 지형/도로 {terrain.Id.Value}의 삼각분할 불가 극소면 {area:F8}m² 제외. 도로 전체 실제 피복 면적을 별도 확인합니다.");continue;}
                throw new InvalidDataException($"지형/도로 {terrain.Id.Value} 면 {area:F8}m²의 실제 삼각분할을 읽지 못했습니다.");
            }
            for(var i=0;i<mesh.NumTriangles;i++)
            {
                var tri=mesh.get_Triangle(i)??throw new InvalidDataException($"지형/도로 {terrain.Id.Value} 면의 삼각형 {i}가 비어 있습니다.");
                triangles.Add(new(ModelBuilder.Meters(tri.get_Vertex(0)),ModelBuilder.Meters(tri.get_Vertex(1)),ModelBuilder.Meters(tri.get_Vertex(2))));
            }
        }
        if(triangles.Count==0)throw new InvalidDataException("지형 상부 TIN을 읽을 수 없습니다.");return new(triangles);
    }
    public static IList<CurveLoop> Loops(Polygon polygon,double z,double tolerance)
    {
        var result=new List<CurveLoop>();
        var rings=new[]{polygon.ExteriorRing}.Concat(polygon.InteriorRings).ToList();
        for(var ri=0;ri<rings.Count;ri++)
        {
            var ring=rings[ri];
            var points=new List<XYZ>();foreach(var c in ring.Coordinates.SkipLast(1))
            {var p=ModelBuilder.Xyz(new(c.X,c.Y,z));if(points.Count==0 || points[^1].DistanceTo(p)>tolerance*1.05)points.Add(p);}
            if(points.Count>1 && points[^1].DistanceTo(points[0])<=tolerance*1.05)points.RemoveAt(points.Count-1);
            if(points.Count<3)
            {
                if(ri>0 && Polygons.Factory.CreatePolygon(ring.Coordinates).Area<.01)continue;
                throw new InvalidDataException($"Revit 최소 선분보다 작은 면 경계입니다 (외곽/구멍 {ri}, 면적 {Polygons.Factory.CreatePolygon(ring.Coordinates).Area:F8}m²).");
            }
            var loop=ModelBuilder.Loop(points);if(loop.IsCounterclockwise(XYZ.BasisZ)!=(ri==0))loop.Flip();result.Add(loop);
        }
        return result;
    }
}
public sealed class ContextFailures(List<string> log):IFailuresPreprocessor
{
    public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
    {
        var error=false;foreach(var failure in accessor.GetFailureMessages())
        {
            log.Add("Revit: "+failure.GetDescriptionText());
            if(failure.GetSeverity()==FailureSeverity.Warning)accessor.DeleteWarning(failure);else error=true;
        }
        return error?FailureProcessingResult.ProceedWithRollBack:FailureProcessingResult.Continue;
    }
}
public static class Views
{
    public static View3D Create(Document doc,string name,IEnumerable<Point3> points)
    {
        var type=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v=>v.ViewFamily==ViewFamily.ThreeDimensional);
        var view=View3D.CreateIsometric(doc,type.Id);var names=new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v=>v.Name).ToHashSet();
        var candidate=name;var i=2;while(names.Contains(candidate))candidate=name+" "+i++;view.Name=candidate;view.DetailLevel=ViewDetailLevel.Fine;view.DisplayStyle=DisplayStyle.ShadingWithEdges;
        var list=points.ToList();var box=new BoundingBoxXYZ {Min=ModelBuilder.Xyz(new(list.Min(p=>p.X)-3,list.Min(p=>p.Y)-3,list.Min(p=>p.Z)-10)),Max=ModelBuilder.Xyz(new(list.Max(p=>p.X)+3,list.Max(p=>p.Y)+3,list.Max(p=>p.Z)+60))};view.SetSectionBox(box);
        view.SetOrientation(new ViewOrientation3D(new XYZ(0,-100,100),new XYZ(0,1,1).Normalize(),new XYZ(0,1,-1).Normalize()));
        var mass=doc.Settings.Categories.get_Item(BuiltInCategory.OST_Mass);if(view.CanCategoryBeHidden(mass.Id))view.SetCategoryHidden(mass.Id,false);return view;
    }
}
