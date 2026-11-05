using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CivilBim.Core;
using NetTopologySuite.Geometries;
using System.Text.Json;

namespace CivilBim.Revit;
public sealed class Revision05CheckStartup:IExternalApplication
{
    public Result OnStartup(UIControlledApplication controlled)
    {
        var folder=Environment.GetEnvironmentVariable("CIVILBIM_REV05_CHECK");if(string.IsNullOrEmpty(folder))return Result.Succeeded;var done=false;
        controlled.Idling+=(sender,_)=>{if(done||sender is not UIApplication ui)return;done=true;try{Run(ui,folder);}catch(Exception e){File.WriteAllText(Path.Combine(folder,"check.json"),JsonSerializer.Serialize(new{Status="failed",Error=e.ToString()}));}};return Result.Succeeded;
    }
    private static void Run(UIApplication ui,string folder)
    {
        var doc=ui.OpenAndActivateDocument(Path.Combine(folder,"프로젝트-복사본.rvt")).Document;var cad=new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().First();
        var symbols=new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();var cip=symbols.First(s=>s.FamilyName=="C_ER_CIP"&&s.Name.Contains("530"));var hp=symbols.First(s=>s.FamilyName=="Pile-H형강");
        (string Length,string Top) Params(FamilySymbol s){var n=VerticalWindow.FamilyNames(doc,s);return(VerticalWindow.Find(n,"Depth","깊이","길이","Length","높이")??throw new InvalidDataException(s.FamilyName+": 길이 자동탐색 실패: "+string.Join(", ",n)),VerticalWindow.Find(n,"TOP","상단EL","상단 표고")??"");}
        var cp=Params(cip);var hpp=Params(hp);var settings=new Settings{CipLayer="_CIP",HPileLayer="_측면말뚝",CipFamily=cip.UniqueId,HPileFamily=hp.UniqueId,CipLengthParameter=cp.Length,CipTopParameter=cp.Top,HPileLengthParameter=hpp.Length,HPileTopParameter=hpp.Top};
        var c=CadReader.Read(doc,cad);var circle=c.Circles.First(x=>x.Layer=="_CIP");c.Circles.RemoveAll(x=>x!=circle);
        var h=CadReader.Read(doc,cad);var item=h.HPiles(settings.HPileLayer).First();var origin=ModelBuilder.Xyz(item.Center);h.Crop(origin-new XYZ(8,8,0),origin+new XYZ(8,8,0));
        var counts=new Dictionary<string,int>();using(var g=new TransactionGroup(doc,"CIP HPILE 빈 좌표계 시험"))
        {
            g.Start();List<FamilyInstance> instances;using(var t=new Transaction(doc,"수직부재 배치")){t.Start();var cips=VerticalService.Place(doc,c,"CIP",settings,"TEST-CRS");var piles=VerticalService.Place(doc,h,"H-PILE",settings,"TEST-CRS");instances=cips.Concat(piles).ToList();counts["CIP"]=cips.Count;counts["HPile"]=piles.Count;if(t.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("시험 배치 실패");}
            var details=instances.Select(p=>{var b=FamilyBuilder.Bounds(p);var i=Storage.Info(p)!;return new{Id=p.Id.Value,Family=p.Symbol.FamilyName,Kind=i.Kind,ExpectedTop=i.Top,ExpectedBottom=i.Bottom,ActualTop=b.Max.Z*.3048,ActualBottom=b.Min.Z*.3048,Joins=JoinGeometryUtils.GetJoinedElements(doc,p).Select(x=>new{Id=x.Value,Name=doc.GetElement(x)?.Name,Category=doc.GetElement(x)?.Category?.Name}).ToList()};}).ToList();File.WriteAllText(Path.Combine(folder,"placed.json"),JsonSerializer.Serialize(details,new JsonSerializerOptions{WriteIndented=true}));
            FamilyBuilder.FinalizeVertical(doc,instances,settings);if(instances.Any(p=>Storage.Info(p)!.Frame.Crs!=""))throw new InvalidDataException("빈 좌표계가 변형되었습니다.");g.Assimilate();
        }
        var shape=new[]{Polygons.Factory.CreatePolygon(new[]{new Coordinate(500,500),new(510,500),new(510,510),new(500,510),new(500,500)}),Polygons.Factory.CreatePolygon(new[]{new Coordinate(510,500),new(520,500),new(520,510),new(510,510),new(510,500)})};
        var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First();var tt=new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).Cast<ToposolidType>().First();var created=ExcavationBoundaryWindow.CreateElements(doc,shape,false,tt.Id,level,-6.86);if(created.Count!=2)throw new InvalidDataException("여러 구역 생성 실패");
        var soilCount=0;if(File.Exists(Path.Combine(folder,"report.pdf")))
        {
            var data=BoreholePdf.ExtractCandidates(Path.Combine(folder,"report.pdf"),"EPSG:5186");data.Reviewed=true;
            var soil=SoilBuilder.GenerateStandalone(doc,data,settings);soilCount=soil.Elements.Count;
            if(soilCount==0||soil.Elements.Any(id=>doc.GetElement(id) is not Toposolid))throw new InvalidDataException("편집 가능한 지층 생성 실패");
        }
        File.WriteAllText(Path.Combine(folder,"check.json"),JsonSerializer.Serialize(new{Status="passed",CipFamily=cip.FamilyName,CipParameters=new{cp.Length,cp.Top},HPileFamily=hp.FamilyName,HPileParameters=new{hpp.Length,hpp.Top},Counts=counts,EmptyCrs=true,CreatedRegions=created.Count,SoilToposolids=soilCount},new JsonSerializerOptions{WriteIndented=true}));
        doc.SaveAs(Path.Combine(folder,"복사본-검증결과.rvt"),new SaveAsOptions{OverwriteExistingFile=true});
    }
    public Result OnShutdown(UIControlledApplication controlled)=>Result.Succeeded;
}
