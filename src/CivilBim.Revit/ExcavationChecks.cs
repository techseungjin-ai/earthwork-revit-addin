using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CivilBim.Core;
using NetTopologySuite.Geometries;
using System.Text.Json;

namespace CivilBim.Revit;
public sealed class ExcavationCheckStartup:IExternalApplication
{
    public Result OnStartup(UIControlledApplication controlled)
    {
        var folder=Environment.GetEnvironmentVariable("CIVILBIM_EXC_CHECK");if(string.IsNullOrEmpty(folder))return Result.Succeeded;bool done=false;
        controlled.Idling+=(sender,_)=>{if(done||sender is not UIApplication ui)return;done=true;try{Run(ui,folder);}catch(Exception e){File.WriteAllText(Path.Combine(folder,"check.json"),JsonSerializer.Serialize(new{Status="failed",Error=e.ToString()}));}};return Result.Succeeded;
    }
    private static void Run(UIApplication ui,string folder)
    {
        var path=Path.Combine(folder,"프로젝트-복사본.rvt");var doc=ui.OpenAndActivateDocument(path).Document;
        var cad=new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().First();var reader=CadReader.Read(doc,cad);
        var closed=reader.Paths.Where(p=>p.Layer=="_건축"&&p.Closed).ToList();if(closed.Count==0)throw new InvalidDataException("실제 CAD 폐합 경계가 없습니다.");
        var source=closed.OrderByDescending(p=>p.Points.Count).First();var coords=source.Points.Select(ModelBuilder.Meters).Select(p=>new Coordinate(p.X,p.Y)).ToArray();var poly=Polygons.Factory.CreatePolygon(coords);if(!poly.IsValid||poly.Area<.01)throw new InvalidDataException("선택된 실제 폐합선이 유효하지 않습니다.");
        var bounds=poly.EnvelopeInternal;var half=Polygons.Factory.ToGeometry(new Envelope(bounds.MinX,bounds.Centre.X,bounds.MinY-1,bounds.MaxY+1));var cut=Polygons.Enumerate(poly.Intersection(half)).ToList();if(cut.Count==0||cut.Sum(p=>p.Area)>=poly.Area)throw new InvalidDataException("중심경계 교차 시험 실패");
        var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First();var ft=new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().First(t=>!t.IsFoundationSlab);var tt=new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).Cast<ToposolidType>().First();
        var floors=ExcavationBoundaryWindow.CreateElements(doc,cut,true,ft.Id,level,-6.86);var tops=ExcavationBoundaryWindow.CreateElements(doc,cut,false,tt.Id,level,-9.4);
        var boundary=Polygons.Factory.CreatePolygon(new[]{new Coordinate(99,17),new(149,17),new(149,79),new(99,79),new(99,17)});
        var paths=reader.Paths.Where(p=>p.Layer=="CE-SLOP-FILL").Select(p=>(IReadOnlyList<Point3>)p.Points.Select(ModelBuilder.Meters).ToList()).ToList();
        var regions=ExcavationRegions.Split(paths,boundary);if(regions.Count<2)throw new InvalidDataException("실제 사면 구분선의 구역 분할 실패");
        var surface=ExcavationBoundaryWindow.CreateElements(doc,new[]{regions[0]},false,tt.Id,level,-5.66);
        using(var tx=new Transaction(doc,"지형 하위요소 편집 시험"))
        {
            tx.Start();var topo=(Toposolid)doc.GetElement(surface[0]);var editor=topo.GetSlabShapeEditor()??throw new InvalidDataException("지형 하위요소 편집기를 사용할 수 없습니다.");if(!editor.IsEnabled)editor.Enable();doc.Regenerate();
            var point=regions[0].InteriorPoint.Coordinate;var vertex=editor.AddPoint(ModelBuilder.Xyz(new(point.X,point.Y,-5.66)))??throw new InvalidDataException("지형 편집점을 추가하지 못했습니다.");editor.ModifySubElement(vertex,ModelBuilder.Feet(.5));doc.Regenerate();tx.RollBack();
        }
        var result=new{Status="passed",Layer="_건축",Closed=closed.Count,OriginalArea=poly.Area,ClippedArea=cut.Sum(x=>x.Area),Floors=floors.Count,Tops=tops.Count,FloorEL=-6.86,TopEL=-9.4,ActualLayer="CE-SLOP-FILL",Segments=paths.Count,Regions=regions.Count,SelectedArea=regions[0].Area,Created=surface.Count,ShapeEdit="점 추가 및 표고 변경 후 원상복구"};File.WriteAllText(Path.Combine(folder,"check.json"),JsonSerializer.Serialize(result));
        doc.SaveAs(Path.Combine(folder,"굴착경계-검증.rvt"),new SaveAsOptions{OverwriteExistingFile=true});
    }
    public Result OnShutdown(UIControlledApplication controlled)=>Result.Succeeded;
}
