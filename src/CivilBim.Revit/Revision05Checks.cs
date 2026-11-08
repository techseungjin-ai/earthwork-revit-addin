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
        controlled.Idling+=(sender,_)=>{if(sender is not UIApplication ui)return;if(done){if(File.Exists(Path.Combine(folder,"exercise-request.json"))&&!File.Exists(Path.Combine(folder,"exercise.json")))try{ExerciseSections(ui,folder);}catch(Exception e){File.WriteAllText(Path.Combine(folder,"exercise.json"),JsonSerializer.Serialize(new{Status="failed",Error=e.ToString()}));}return;}done=true;try{Run(ui,folder);}catch(Exception e){File.WriteAllText(Path.Combine(folder,"check.json"),JsonSerializer.Serialize(new{Status="failed",Error=e.ToString()}));}};return Result.Succeeded;
    }
    private static void Run(UIApplication ui,string folder)
    {
        if(File.Exists(Path.Combine(folder,"support.flag"))){CheckSupport(ui,folder);return;}
        if(File.Exists(Path.Combine(folder,"terrain.flag"))){TerrainTransferChecks.Run(ui,folder);return;}
        if(File.Exists(Path.Combine(folder,"inspect.flag"))){InspectSections(ui,folder);return;}
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
    private static void CheckSupport(UIApplication ui,string folder)
    {
        var doc=ui.OpenAndActivateDocument(Path.Combine(folder,"프로젝트-복사본.rvt")).Document;
        var symbols=new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
        File.WriteAllText(Path.Combine(folder,"families.json"),JsonSerializer.Serialize(symbols.Select(s=>new{
            Family=s.FamilyName,Type=s.Name,Category=s.Category?.Name,Placement=s.Family.FamilyPlacementType.ToString(),SupportedPoint=FamilyBuilder.IsPointGeneric(s)
        }),new JsonSerializerOptions{WriteIndented=true}));
        var checks=new List<object>();
        var settings=new Settings();
        var line=new PlanSegment(new(25,25,0),new(29,25,0));
        void Check(string kind,FamilySymbol? symbol,Func<FamilySymbol,List<FamilyInstance>> place)
        {
            if(symbol==null){checks.Add(new{Kind=kind,Status="family-missing"});return;}
            try
            {
                using var t=new Transaction(doc,kind+" 배치 검증");t.Start();
                var members=place(symbol);
                if(t.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("커밋 실패");
                checks.Add(new{Kind=kind,Status="passed",Family=symbol.FamilyName,Count=members.Count,Positions=members.Select(e=>e.Location is LocationPoint p?(Point3?)ModelBuilder.Meters(p.Point):null).ToArray()});
            }
            catch(Exception ex){checks.Add(new{Kind=kind,Status="failed",Error=ex.Message});}
        }
        Check("STRUT",symbols.FirstOrDefault(s=>s.Family.FamilyPlacementType==FamilyPlacementType.CurveDrivenStructural&&s.Name.Contains("W1100"))
            ??symbols.FirstOrDefault(s=>s.Family.FamilyPlacementType==FamilyPlacementType.CurveDrivenStructural),
            s=>HorizontalService.Place(doc,new[]{line},new[]{3.0},s.UniqueId,settings,"NOV08-STRUT"));
        if(File.Exists(Path.Combine(folder,"direct.flag")))
        {
            var import=new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
                .First(i=>(doc.GetElement(i.GetTypeId())?.Name??i.Name).Contains("굴착계획평면도"));
            var reader=CadReader.Read(doc,import);
            var layer=reader.Layers.First(l=>l.Contains("사보강재"));
            var candidates=SheetHorizontalWindow.DirectCandidates(reader,layer,false);
            var target=candidates.MaxBy(s=>s.Length)??throw new InvalidDataException("실제 도면에 사보강재 직선이 없습니다.");
            var selected=HorizontalRecognition.PickSegment(candidates,target.Mid);
            if(HorizontalRecognition.SegmentKey(selected)!=HorizontalRecognition.SegmentKey(target))throw new InvalidDataException("선택한 사보강재와 인식 결과가 다릅니다.");
            File.WriteAllText(Path.Combine(folder,"direct.json"),JsonSerializer.Serialize(new{Layer=layer,CandidateCount=candidates.Count,SelectedCount=1,Length=selected.Length}));
            Check("STRUT 직접 선택",symbols.FirstOrDefault(s=>s.FamilyName.Contains("수평부재")&&s.Name.Contains("버팀대")),
                s=>HorizontalService.Place(doc,new[]{selected},new[]{9.844,4.268,-4.007},s.UniqueId,settings,"NOV08-DIRECT"));
        }
        FamilySymbol? Find(string pattern)=>symbols.FirstOrDefault(s=>FamilyBuilder.IsPointGeneric(s)&&System.Text.RegularExpressions.Regex.IsMatch(s.FamilyName+" "+s.Name,pattern,System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        Check("어스앵커",Find("어스|earth|anchor|앵커"),s=>HorizontalService.PlacePoints(doc,new[]{line},new[]{4.0,5.0},s.UniqueId,settings,"NOV08-ANCHOR","어스앵커",.05,true,0));
        Check("락볼트",Find("락볼트|록볼트|rock.?bolt"),s=>HorizontalService.PlacePoints(doc,new[]{line},new[]{6.0,7.0},s.UniqueId,settings,"NOV08-BOLT","락볼트",.1,false,0));
        File.WriteAllText(Path.Combine(folder,"check.json"),JsonSerializer.Serialize(checks,new JsonSerializerOptions{WriteIndented=true}));
        doc.SaveAs(Path.Combine(folder,"testtest-검증결과.rvt"),new SaveAsOptions{OverwriteExistingFile=true});
    }
    private static void InspectSections(UIApplication ui,string folder)
    {
        var doc=ui.OpenAndActivateDocument(Path.Combine(folder,"프로젝트-복사본.rvt")).Document;
        var curves=new FilteredElementCollector(doc).OfClass(typeof(CurveElement)).Cast<CurveElement>().Where(c=>c.GeometryCurve is Line).ToList();
        var views=new FilteredElementCollector(doc).OfClass(typeof(ViewSection)).Cast<ViewSection>().Where(v=>!v.IsTemplate).ToList();
        var viewports=new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().ToList();
        var output=new {Status="inspected",Curves=curves.Select(c=>new{Id=c.Id.Value,Owner=c.OwnerViewId.Value,c.ViewSpecific,Style=c.LineStyle.Name,A=ModelBuilder.Meters(c.GeometryCurve.GetEndPoint(0)),B=ModelBuilder.Meters(c.GeometryCurve.GetEndPoint(1)),Length=c.GeometryCurve.Length*.3048}),Sections=views.Select(v=>new{Id=v.Id.Value,v.Name,v.Scale,Origin=ModelBuilder.Meters(v.Origin),Right=ModelBuilder.Meters(v.RightDirection),CropWidth=(v.CropBox.Max.X-v.CropBox.Min.X)*.3048,Members=SectionSheetData.Read(v)?.Members.Count}),Viewports=viewports.Select(v=>new{Id=v.Id.Value,View=v.ViewId.Value,Sheet=v.SheetId.Value,Min=ModelBuilder.Meters(v.GetBoxOutline().MinimumPoint),Max=ModelBuilder.Meters(v.GetBoxOutline().MaximumPoint)}),Families=new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().GroupBy(p=>p.Symbol.FamilyName).Select(g=>new{Family=g.Key,Count=g.Count()})};
        File.WriteAllText(Path.Combine(folder,"inspection.json"),JsonSerializer.Serialize(output,new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(folder,"check.json"),"{\"Status\":\"inspected\"}");
    }
    private sealed record SectionRequest(List<long> Curves,string Prefix);
    private static void ExerciseSections(UIApplication ui,string folder)
    {
        var request=JsonSerializer.Deserialize<SectionRequest>(File.ReadAllText(Path.Combine(folder,"exercise-request.json")))!;var doc=ui.ActiveUIDocument.Document;
        var path=ConnectedPath.Join(request.Curves.Select(id=>(IReadOnlyList<Point3>)((CurveElement)doc.GetElement(new ElementId(id))).GeometryCurve.Tessellate().Select(ModelBuilder.Meters).ToList()));
        var old=new FilteredElementCollector(doc).OfClass(typeof(ViewSection)).Cast<ViewSection>().Where(v=>v.Name.StartsWith(request.Prefix)).ToList();var members=old.SelectMany(v=>SectionSheetData.Read(v)?.Members??[]).Distinct().Select(uid=>doc.GetElement(uid).Id).ToList();
        var before=new FilteredElementCollector(doc).OfClass(typeof(ViewSection)).Select(v=>v.Id).ToHashSet();var window=new SectionSheetWindow(doc);window.ConfigureCheck(path,members);window.CreateCheck(ui);
        var created=new FilteredElementCollector(doc).OfClass(typeof(ViewSection)).Cast<ViewSection>().Where(v=>!before.Contains(v.Id)).ToList();var total=path.Zip(path.Skip(1),(a,b)=>a.DistanceXY(b)).Sum();var width=created.Sum(v=>(v.CropBox.Max.X-v.CropBox.Min.X)*.3048);
        if(Math.Abs(total-width)>.001)throw new InvalidDataException("경로와 단면폭합계 불일치");
        var report=new{Status="passed",PathLength=total,CropWidthSum=width,OldCropWidthSum=old.Sum(v=>(v.CropBox.Max.X-v.CropBox.Min.X)*.3048),Sections=created.Count,Members=members.Count,FoundationVisible=created.All(v=>!v.GetCategoryHidden(new ElementId(BuiltInCategory.OST_StructuralFoundation))),LinesVisible=created.All(v=>!v.GetCategoryHidden(new ElementId(BuiltInCategory.OST_Lines)))};
        File.WriteAllText(Path.Combine(folder,"exercise.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));doc.SaveAs(Path.Combine(folder,"TEST-경로검증결과.rvt"),new SaveAsOptions{OverwriteExistingFile=true});
    }
}
