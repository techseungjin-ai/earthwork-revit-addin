using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CivilBim.Core;
using System.Text.Json;

namespace CivilBim.Revit;

// Local execution helper for an explicitly requested real drawing job; inactive without its config.
public sealed class DrawingModelStartup:IExternalApplication
{
    public Result OnStartup(UIControlledApplication app)
    {
        var file=Environment.GetEnvironmentVariable("CIVILBIM_DRAWING_JOB");if(string.IsNullOrEmpty(file))return Result.Succeeded;
        DrawingModelRunner? runner=null;app.Idling+=(sender,_)=>{if(sender is not UIApplication ui)return;if(runner==null)runner=new(ui,file);runner.Tick();};return Result.Succeeded;
    }
    public Result OnShutdown(UIControlledApplication app)=>Result.Succeeded;
}
public sealed class DrawingModelRunner
{
    public sealed record Config(string Copy,string Output,bool Horizontal=false);
    public sealed record HorizontalGroup(string Name,string Family,List<PlanSegment> Lines,List<double> Levels);
    public sealed record HorizontalPlan(List<HorizontalGroup> Groups);
    public sealed record RouteMap(string Name,List<Point3> Path,List<ProfileMember> Cip,List<ProfileMember> HPile);
    public sealed record Plan(string Cad,string CipLayer,string HPileLayer,string CipFamily,string HPileFamily,string CipLength,string HPileLength,string CipTop,string HPileTop,Point3 Min,Point3 Max,List<Point3> Route,List<ProfileMember> CipProfile,List<ProfileMember> HPileProfile,List<RouteMap>? Maps=null);
    private readonly UIApplication app;private readonly Config config;private Document doc=null!;private bool inspected,finished;private DateTime lastPlan;private readonly List<string> log=[];
    public DrawingModelRunner(UIApplication ui,string file){app=ui;config=JsonSerializer.Deserialize<Config>(File.ReadAllText(file))!;}
    private string FilePath(string name)=>Path.Combine(config.Output,name);
    private void Status(string state,Exception? e=null)=>File.WriteAllText(FilePath("result.json"),JsonSerializer.Serialize(new{Status=state,Log=log,Error=e?.ToString()},BoreholeSet.JsonOptions));
    public void Tick()
    {
        if(finished)return;try
        {
            if(!inspected){inspected=true;Inspect();return;}
            var plan=FilePath(config.Horizontal?"horizontal-plan.json":"placement-plan.json");if(!File.Exists(plan)||File.GetLastWriteTimeUtc(plan)<=lastPlan)return;lastPlan=File.GetLastWriteTimeUtc(plan);if(config.Horizontal)BuildHorizontal(JsonSerializer.Deserialize<HorizontalPlan>(File.ReadAllText(plan),BoreholeSet.JsonOptions)!);else Build(JsonSerializer.Deserialize<Plan>(File.ReadAllText(plan),BoreholeSet.JsonOptions)!);finished=true;
        }catch(Exception e){Status("failed",e);}
    }
    private void Inspect()
    {
        Status("opening");var options=new OpenOptions();using(var basic=BasicFileInfo.Extract(config.Copy))if(basic.IsWorkshared)options.DetachFromCentralOption=DetachFromCentralOption.DetachAndDiscardWorksets;
        doc=app.OpenAndActivateDocument(ModelPathUtils.ConvertUserVisiblePathToModelPath(config.Copy),options,false).Document;
        if(!new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Any())
        {
            using var t=new Transaction(doc,"요청 흙막이 DWG 참조 링크");t.Start();
            var view=new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().FirstOrDefault(v=>!v.IsTemplate&&v.ViewType==ViewType.FloorPlan);
            if(view==null){var type=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v=>v.ViewFamily==ViewFamily.FloorPlan);var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l=>Math.Abs(l.Elevation)).First();view=ViewPlan.Create(doc,type.Id,level.Id);view.Name="흙막이_도면참조_평면";}
            var dwg=Path.Combine(Path.GetDirectoryName(config.Copy)!,"흙막이 도면_261019.dwg");if(!doc.Link(dwg,new DWGImportOptions{Placement=ImportPlacement.Origin,Unit=ImportUnit.Millimeter,ThisViewOnly=false},view,out var id))throw new InvalidDataException("참조 DWG 링크 실패");t.Commit();log.Add("빈 프로젝트 복사본에 요청 DWG 원점/mm 링크 (공유좌표 변경 없음)");
        }
        var cads=new List<object>();
        foreach(var cad in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
        {
            var data=CadReader.Read(doc,cad);var transform=cad.GetTotalTransform();var file=FilePath("cad-"+cad.Id.Value+".json");File.WriteAllText(file,JsonSerializer.Serialize(new{Paths=data.Paths.Select(p=>new{p.Layer,p.Closed,Points=p.Points.Select(ModelBuilder.Meters).ToList(),p.BlockKey}),Circles=data.Circles.Select(c=>new{c.Layer,Center=ModelBuilder.Meters(c.Center),Radius=c.Radius*.3048}),Blocks=data.Blocks.Select(b=>new{b.Layer,Origin=ModelBuilder.Meters(b.Origin),b.Angle,b.Key})},BoreholeSet.JsonOptions));
            cads.Add(new{Id=cad.Id.Value,cad.UniqueId,Name=doc.GetElement(cad.GetTypeId())?.Name,cad.IsLinked,Transform=new{Origin=ModelBuilder.Meters(transform.Origin),X=ModelBuilder.Meters(transform.BasisX),Y=ModelBuilder.Meters(transform.BasisY)},data.Layers,GeometryFile=file});
        }
        var families=new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().Where(s=>ScreenChoices.Vertical(s)||ScreenChoices.Horizontal(s)).Select(s=>new{s.UniqueId,s.FamilyName,s.Name,Placement=s.Family.FamilyPlacementType.ToString(),Parameters=s.Parameters.Cast<Parameter>().Select(p=>new{Name=p.Definition.Name,Value=p.AsValueString()??(p.StorageType==StorageType.String?p.AsString():null)}).ToList()}).ToList();
        var siteElements=new FilteredElementCollector(doc).WhereElementIsNotElementType().Where(e=>e is Floor or Toposolid).Select(e=>{
            var box=e.get_BoundingBox(null);var sketchId=e is Floor f?f.SketchId:((Toposolid)e).SketchId;var sketch=doc.GetElement(sketchId) as Sketch;
            var loops=sketch?.Profile.Cast<CurveArray>().Select(a=>a.Cast<Curve>().Select(c=>c.Tessellate().Select(ModelBuilder.Meters).ToList()).ToList()).ToList();
            return new{Id=e.Id.Value,Category=e.Category?.Name,Type=doc.GetElement(e.GetTypeId())?.Name,Level=doc.GetElement(e.LevelId)?.Name,Min=box==null?(Point3?)null:ModelBuilder.Meters(box.Min),Max=box==null?(Point3?)null:ModelBuilder.Meters(box.Max),Loops=loops};}).ToList();
        File.WriteAllText(FilePath("inspection.json"),JsonSerializer.Serialize(new{Settings=Storage.Load(doc),Cads=cads,Families=families,SiteElements=siteElements,Views=new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v=>!v.IsTemplate).Select(v=>new{Id=v.Id.Value,v.Name,Type=v.ViewType.ToString()}),Generated=Storage.Generated(doc).GroupBy(g=>g.Info.Kind).Select(g=>new{Kind=g.Key,Count=g.Count()})},BoreholeSet.JsonOptions));log.Add("요청 RVT 복사본의 CAD/패밀리/기존 모델 조사 완료");Status("awaiting-drawing-plan");
    }
    private void BuildHorizontal(HorizontalPlan plan)
    {
        if(plan.Groups.Count==0||plan.Groups.Any(g=>g.Lines.Count==0||g.Levels.Count==0))throw new InvalidDataException("검토한 수평부재 중심선과 높이가 필요합니다.");
        var settings=Storage.Load(doc);var vertical=Storage.Generated(doc).Where(g=>g.Info.Kind is "CIP" or "HPile").Select(g=>(FamilyInstance)g.Element).ToDictionary(p=>p.Id,FamilyBuilder.Bounds);
        var placed=new List<FamilyInstance>();var reports=new List<object>();using var group=new TransactionGroup(doc,"실제 도면 띠장 사보강재 배치 검산");group.Start();
        using(var t=new Transaction(doc,"검토한 도면 중심선 수평부재 배치"))
        {
            t.Start();foreach(var item in plan.Groups)
            {
                if(doc.GetElement(item.Family) is not FamilySymbol symbol||!ScreenChoices.Horizontal(symbol))throw new InvalidDataException(item.Name+": 선 기반 수평 패밀리 유형이 필요합니다.");
                var source="HORIZONTAL-DWG-261019:"+item.Name;var prior=Storage.Generated(doc).Where(g=>g.Info.Source==source).Select(g=>g.Element.Id).ToList();if(prior.Count>0)doc.Delete(prior);
                var batch=HorizontalService.Place(doc,item.Lines,item.Levels,item.Family,settings,source);placed.AddRange(batch);log.Add(item.Name+": "+batch.Count+"개");
            }
            if(t.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("수평부재 배치 취소");
        }
        foreach(var p in placed){var b=FamilyBuilder.Bounds(p);var actual=(b.Min.Z+b.Max.Z)*.5*.3048+settings.Frame.Elevation;var info=Storage.Info(p)!;if(Math.Abs(actual-info.Top)>.002)throw new InvalidDataException(p.Id+": 실제 수평부재 중심 EL 불일치");reports.Add(new{Id=p.Id.Value,info.Source,Family=p.Symbol.FamilyName,Type=p.Name,Expected=info.Top,Actual=actual,Volume=FamilyBuilder.Solids(p.get_Geometry(new Options{DetailLevel=ViewDetailLevel.Fine})).Sum(s=>s.Volume)*Math.Pow(.3048,3)});}
        foreach(var (id,before) in vertical){var after=FamilyBuilder.Bounds((FamilyInstance)doc.GetElement(id));if(before.Min.DistanceTo(after.Min)>.001||before.Max.DistanceTo(after.Max)>.001)throw new InvalidDataException(id+": 기존 수직부재 형상 변경 감지");}
        group.Assimilate();File.WriteAllText(FilePath("horizontal-actual.json"),JsonSerializer.Serialize(reports,BoreholeSet.JsonOptions));
        using(var t=new Transaction(doc,"수평부재 검토뷰")){t.Start();var points=placed.SelectMany(p=>{var b=FamilyBuilder.Bounds(p);return new[]{ModelBuilder.Meters(b.Min),ModelBuilder.Meters(b.Max)};}).Concat(vertical.Values.SelectMany(b=>new[]{ModelBuilder.Meters(b.Min),ModelBuilder.Meters(b.Max)})).ToList();var view=Views.Create(doc,"흙막이_띠장_사보강재_검토",points);foreach(var import in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)))if(import.CanBeHidden(view))view.HideElements(new[]{import.Id});t.Commit();RevisionRunner.Export(doc,view,FilePath("02-수평부재모델"));}
        doc.SaveAs(FilePath("이걸로 검증_띠장-사보강재_전개도적용.rvt"),new SaveAsOptions{OverwriteExistingFile=true});log.Add($"수평부재 {placed.Count}개 실제 중심높이 검산 / 기존 수직 {vertical.Count}개 형상 유지");Status("completed");
    }
    private static (double Top,double Bottom) Height(double station,List<ProfileMember> profile)
    {
        if(profile.Count<2||station<profile[0].Station-.05||station>profile[^1].Station+.05)throw new InvalidDataException($"전개도 구간 밖 거리 {station:F3}m");
        station=Math.Clamp(station,profile[0].Station,profile[^1].Station);var right=profile.FindIndex(p=>p.Station>=station);if(right<=0)return(profile[0].Top,profile[0].Bottom);var a=profile[right-1];var b=profile[right];var t=(station-a.Station)/(b.Station-a.Station);return(a.Top+t*(b.Top-a.Top),a.Bottom+t*(b.Bottom-a.Bottom));
    }
    private void Build(Plan plan)
    {
        Status("building");var settings=Storage.Load(doc);settings.CipFamily=plan.CipFamily;settings.HPileFamily=plan.HPileFamily;settings.CipLayer=plan.CipLayer;settings.HPileLayer=plan.HPileLayer;settings.CipLengthParameter=plan.CipLength;settings.HPileLengthParameter=plan.HPileLength;settings.CipTopParameter=plan.CipTop;settings.HPileTopParameter=plan.HPileTop;
        var maps=plan.Maps??[new("주 전개도",plan.Route,plan.CipProfile,plan.HPileProfile)];var profiles=maps.SelectMany(m=>m.Cip.Concat(m.HPile)).ToList();settings.PileTop=profiles.Max(p=>p.Top);settings.PileBottom=profiles.Min(p=>p.Bottom);
        var cad=doc.GetElement(plan.Cad) as ImportInstance??throw new InvalidDataException("선택 CAD 없음");var reader=CadReader.Read(doc,cad);reader.Crop(ModelBuilder.Xyz(plan.Min),ModelBuilder.Xyz(plan.Max));var source="DESKTOP-DWG-261019";var report=new List<object>();var placed=new List<FamilyInstance>();var groups=new Dictionary<string,List<FamilyInstance>>();
        using(var t=new Transaction(doc,"흙막이 DWG CIP H-PILE 실제 전개도 모델링"))
        {
            t.Start();var prior=Storage.Generated(doc).Where(g=>g.Info.Source==source).Select(g=>g.Element.Id).ToList();if(prior.Count>0)doc.Delete(prior);
            placed.AddRange(VerticalService.Place(doc,reader,"CIP",settings,source));placed.AddRange(VerticalService.Place(doc,reader,"H-PILE",settings,source));FamilyBuilder.ReleaseVerticalJoins(doc,placed);
            var held=new List<object>();
            foreach(var pile in placed.ToList())
            {
                var info=Storage.Info(pile)!;var b=FamilyBuilder.Bounds(pile);var center=ModelBuilder.Meters(pile.Location is LocationPoint location?location.Point:(b.Min+b.Max)/2);var map=maps.OrderBy(m=>ConnectedPath.Nearest(center,m.Path).Distance).First();
                if(info.Kind=="CIP"){var pending=maps.FirstOrDefault(m=>m.Cip.Count==0&&ConnectedPath.Nearest(center,m.Path).Distance<.3);if(pending!=null)map=pending;}
                if(info.Kind=="CIP"&&map.Cip.Count==0){held.Add(new{info.Kind,Route=map.Name,Center=center,Reason="사용자 결정: 연암 경계 미표기 CIP 하단 확정 보류"});doc.Delete(pile.Id);placed.Remove(pile);continue;}
                var station=DevelopmentMapping.Station(center,map.Path,1);var h=Height(station,info.Kind=="CIP"?map.Cip:map.HPile);FamilyBuilder.Resize(doc,pile,info,h.Top,h.Bottom,settings);
                if(!groups.TryGetValue(map.Name,out var list)){list=[];groups[map.Name]=list;}list.Add(pile);
                report.Add(new{Id=pile.Id.Value,info.Kind,Family=pile.Symbol.FamilyName,Type=pile.Name,Route=map.Name,Center=center,Station=station,h.Top,h.Bottom});
            }
            File.WriteAllText(FilePath("held-members.json"),JsonSerializer.Serialize(held,BoreholeSet.JsonOptions));log.Add($"미확정 CIP 보류: {held.Count}개");
            FamilyBuilder.ReleaseVerticalJoins(doc,placed);foreach(var pile in placed)FamilyBuilder.Track(doc,pile,Storage.Info(pile)!);
            Storage.Write(doc.ProjectInformation,settings);if(t.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("모델링 취소");
        }
        // Revit may create fresh joins at commit; release them in a separate transaction
        // before inspecting geometry, as in the verified section-sheet workflow.
        using(var release=new Transaction(doc,"확정 후 CIP H-PILE 자동결합 해제")){release.Start();var count=FamilyBuilder.ReleaseVerticalJoins(doc,placed);foreach(var pile in placed)FamilyBuilder.Track(doc,pile,Storage.Info(pile)!);release.Commit();log.Add($"커밋 후 자동결합 해제: {count}건");}
        var actual=placed.Select(pile=>{var info=Storage.Info(pile)!;var b=FamilyBuilder.Bounds(pile);return new{Id=pile.Id.Value,info.Kind,ExpectedTop=info.Top,ExpectedBottom=info.Bottom,ActualTop=b.Max.Z*.3048+settings.Frame.Elevation,ActualBottom=b.Min.Z*.3048+settings.Frame.Elevation};}).ToList();File.WriteAllText(FilePath("actual-geometry.json"),JsonSerializer.Serialize(actual,BoreholeSet.JsonOptions));
        foreach(var p in actual)if(Math.Abs(p.ActualBottom-p.ExpectedBottom)>.002||Math.Abs(p.ActualTop-p.ExpectedTop)>.002)throw new InvalidDataException($"{p.Id} 실제 EL 검산 실패: 실제{p.ActualTop:F6}/{p.ActualBottom:F6}, 요청{p.ExpectedTop:F6}/{p.ExpectedBottom:F6}");
        File.WriteAllText(FilePath("placed-members.json"),JsonSerializer.Serialize(report,BoreholeSet.JsonOptions));log.Add($"실제 전개도 높이 적용 및 실제 솔리드 EL 검산: {placed.Count}개");
        foreach(var map in maps.Where(m=>groups.ContainsKey(m.Name))){var window=ModelessHost.Open(app,"요청도면단면",()=>new SectionSheetWindow(doc));window.ConfigureCheck(map.Path,groups[map.Name].Select(p=>p.Id));window.CreateCheck(app);window.Close();}
        using(var t=new Transaction(doc,"검토 3D 뷰")){t.Start();var points=placed.SelectMany(p=>{var b=FamilyBuilder.Bounds(p);return new[]{ModelBuilder.Meters(b.Min),ModelBuilder.Meters(b.Max)};}).ToList();var view=Views.Create(doc,"흙막이_CIP_HPILE_전개도검증",points);foreach(var g in Storage.Generated(doc).Where(g=>g.Info.Source!=source))if(g.Element.CanBeHidden(view))view.HideElements(new[]{g.Element.Id});foreach(var import in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)))if(import.CanBeHidden(view))view.HideElements(new[]{import.Id});t.Commit();RevisionRunner.Export(doc,view,FilePath("01-흙막이모델"));}
        doc.SaveAs(FilePath("이걸로 검증_CIP-HPILE_전개도적용.rvt"),new SaveAsOptions{OverwriteExistingFile=true});Status("completed");
    }
}

