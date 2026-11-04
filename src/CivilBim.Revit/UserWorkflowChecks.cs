using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using CivilBim.Core;
using System.Text.Json;

namespace CivilBim.Revit;

public sealed class UserWorkflowCheckStartup:IExternalApplication
{
    public Result OnStartup(UIControlledApplication app)
    {
        var config=Environment.GetEnvironmentVariable("CIVILBIM_USER_CHECKS");if(string.IsNullOrEmpty(config))return Result.Succeeded;
        UserWorkflowChecks? checks=null;app.Idling+=(sender,_)=>{if(sender is not UIApplication ui)return;if(checks==null){checks=new UserWorkflowChecks(ui,config);checks.Start();}else checks.Tick();};return Result.Succeeded;
    }
    public Result OnShutdown(UIControlledApplication app)=>Result.Succeeded;
}
public sealed class UserWorkflowChecks
{
    public sealed record Config(string Copy,string Boreholes,string Output,bool MembersOnly=true);
    private readonly UIApplication app;private readonly Config config;private readonly List<string> log=[];
    private Document doc=null!;private Settings settings=null!;private BoreholeSet data=null!;private SectionSheetWindow section=null!;private List<FamilyInstance> piles=[];private List<Point3> route=[];
    public UserWorkflowChecks(UIApplication app,string file){this.app=app;config=JsonSerializer.Deserialize<Config>(File.ReadAllText(file))!;}
    private void Report(string state,Exception? error=null)=>File.WriteAllText(Path.Combine(config.Output,"result.json"),JsonSerializer.Serialize(new{Status=state,Log=log,Error=error?.ToString()},BoreholeSet.JsonOptions));
    private void Check(bool condition,string message){if(!condition)throw new InvalidDataException(message);log.Add(message);Report("running");}
    private Action? pending;
    private void Later(Action action)=>pending=action;
    public void Tick(){var action=pending;pending=null;if(action==null)return;try{action();}catch(Exception e){Report("failed",e);}}
    public void Start()
    {
        try
        {
            Report("running");var options=new OpenOptions();using(var basic=BasicFileInfo.Extract(config.Copy))if(basic.IsWorkshared)options.DetachFromCentralOption=DetachFromCentralOption.DetachAndDiscardWorksets;
            doc=app.OpenAndActivateDocument(ModelPathUtils.ConvertUserVisiblePathToModelPath(config.Copy),options,false).Document;settings=Storage.Load(doc);
            if(config.MembersOnly){Later(PathAndSheet);return;}
            data=JsonSerializer.Deserialize<BoreholeSet>(File.ReadAllText(config.Boreholes),BoreholeSet.JsonOptions)!;data.Validate(data.Crs);
            Check(data.Reviewed,"사용자가 검토 저장한 5공 JSON 연결 (OCR 확정 아님)");
            var soil=ModelessHost.Open(app,"NativePDF생성검증",()=>new SoilWindow(doc,settings));soil.Input.Use(config.Boreholes);
            Check(!Controls<System.Windows.Controls.ComboBox>(soil).Any(),"실제 지층창에 대지 선택 ComboBox 없음");
            soil.Generate(app);var validation=SoilValidation.Check(doc,data);File.WriteAllText(Path.Combine(config.Output,"soil-validation.txt"),validation);
            Check(validation.StartsWith("검증 통과"),"실제 PDF 지층: Native 체적/5공 상하단·연속성 검증 통과");
            var generatedView=new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Last(v=>v.Name.StartsWith("02_작업대지 지층"));RevisionRunner.Export(doc,generatedView,Path.Combine(config.Output,"01-시추지층"));
            File.WriteAllText(Path.Combine(config.Output,"borehole-levels.json"),JsonSerializer.Serialize(data.Boreholes.Select(b=>new{b.Id,b.Easting,b.Northing,b.GroundElevation}),BoreholeSet.JsonOptions));
            soil.Close();Later(PathAndSheet);
        }
        catch(Exception e){Report("failed",e);}
    }
    private void PathAndSheet()
    {
        piles=Storage.Generated(doc).Where(g=>g.Info.Kind is "CIP" or "HPile").Select(g=>g.Element).OfType<FamilyInstance>().ToList();
        Check(piles.Count>0,$"실제 프로젝트 수직부재 {piles.Count}개 읽기");
        var usable=new List<FamilyInstance>();var diagnostics=new List<object>();
        foreach(var pile in piles){try{var bounds=FamilyBuilder.Bounds(pile);usable.Add(pile);diagnostics.Add(new{Id=pile.Id.Value,Family=pile.Symbol.FamilyName,Type=pile.Name,Valid=true,Error=""});}catch(Exception e){diagnostics.Add(new{Id=pile.Id.Value,Family=pile.Symbol.FamilyName,Type=pile.Name,Valid=false,Error=e.Message});if(!File.Exists(Path.Combine(config.Output,"invalid-member-detail.json")))Diagnose(pile);}}
        File.WriteAllText(Path.Combine(config.Output,"member-diagnostics.json"),JsonSerializer.Serialize(diagnostics,BoreholeSet.JsonOptions));
        log.Add($"시작 시 솔리드 {usable.Count} / 결합 등으로 없는 부재 {piles.Count-usable.Count}. 생성 전 결합해제로 전체 대상 복구 검사");
        Check(piles.Count>0,"실제 솔리드가 있는 수직부재 검증 대상 확보");
        var centers=piles.Select(p=>{try{var b=FamilyBuilder.Bounds(p);return ModelBuilder.Meters((b.Min+b.Max)/2);}catch{if(p.Location is LocationPoint point)return ModelBuilder.Meters(point.Point);throw;}}).ToList();
        var candidates=new List<(string Cad,string Layer,List<Point3> Points,double Median,double Max)>();
        foreach(var cad in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
        {
            var reader=CadReader.Read(doc,cad);
            foreach(var path in reader.Paths.Where(p=>p.Points.Count>=3))
            {
                var points=path.Points.Select(ModelBuilder.Meters).ToList();if(points.Any(p=>Math.Abs(p.Z-points[0].Z)>.001))continue;
                if(points.Zip(points.Skip(1),(a,b)=>a.DistanceXY(b)).Sum()<40)continue;
                var distances=centers.Select(p=>ConnectedPath.Nearest(p,points).Distance).Order().ToList();candidates.Add((cad.Name,path.Layer,points,distances[distances.Count/2],distances[^1]));
            }
        }
        File.WriteAllText(Path.Combine(config.Output,"path-candidates.json"),JsonSerializer.Serialize(candidates.OrderBy(p=>p.Median).Take(25).Select(p=>new{p.Cad,p.Layer,Count=p.Points.Count,p.Median,p.Max,Points=p.Points}),BoreholeSet.JsonOptions));
        var selected=candidates.OrderBy(p=>p.Median).FirstOrDefault();if(selected.Points==null)throw new InvalidDataException("실제 CAD에서 이어진 경로 후보를 찾지 못했습니다.");route=ConnectedPath.Cad(selected.Points);
        Check(selected.Median<3,$"실제 CAD 경로 후보 {selected.Layer}, {route.Count-1}구간 / 부재 중앙 이격{selected.Median:F3}m / 최대{selected.Max:F3}m");
        // Native creation does not require changing ActiveView (forbidden while Idling).
        var curves=new List<ModelCurve>();
        using(var t=new Transaction(doc,"검증용 실제 경로 Revit 다중선"))
        {
            t.Start();var z=ModelBuilder.Feet(route[0].Z);var plane=SketchPlane.Create(doc,Plane.CreateByNormalAndOrigin(XYZ.BasisZ,new XYZ(0,0,z)));
            for(var i=0;i<route.Count-1;i++){if(route[i].DistanceXY(route[i+1])<.001)continue;curves.Add(doc.Create.NewModelCurve(Line.CreateBound(ModelBuilder.Xyz(route[i]),ModelBuilder.Xyz(route[i+1])),plane));}t.Commit();
        }
        route=ConnectedPath.Join(curves.AsEnumerable().Reverse().Select((c,i)=>(IReadOnlyList<Point3>)(i%2==0?c.GeometryCurve.Tessellate().Reverse():c.GeometryCurve.Tessellate()).Select(ModelBuilder.Meters).ToList()));
        Check(route.Count>2,$"실제 Revit 모델선 {curves.Count}개 순서·방향 뒤섞어 단일 경로 연결");
        section=ModelessHost.Open(app,"Native경로시트검증",()=>new SectionSheetWindow(doc));section.ConfigureCheck(route,piles.Select(p=>p.Id));
        var oldCount=new FilteredElementCollector(doc).OfClass(typeof(ViewSection)).GetElementCount();section.CreateCheck(app);
        Check(piles.All(p=>FamilyBuilder.Bounds(p).Max.Z>FamilyBuilder.Bounds(p).Min.Z),$"결합 해제 후 CIP/H-PILE 전체 {piles.Count}개 실제 솔리드 복구");
        Check(new FilteredElementCollector(doc).OfClass(typeof(ViewSection)).GetElementCount()-oldCount==route.Count-1,"실제 경로 전 구간 단면 생성 및 새 시트 배치");Later(ApplyHeights);
    }
    private void Diagnose(FamilyInstance pile)
    {
        object Params(Element e)=>e.Parameters.Cast<Parameter>().Select(p=>new{Name=p.Definition.Name,Storage=p.StorageType.ToString(),Value=p.StorageType==StorageType.Double?p.AsDouble().ToString("R"):p.StorageType==StorageType.Integer?p.AsInteger().ToString():p.AsValueString()??p.AsString(),ReadOnly=p.IsReadOnly}).ToList();
        var g=pile.get_Geometry(new Options{DetailLevel=ViewDetailLevel.Fine,IncludeNonVisibleObjects=true});
        var geometryTypes=g?.Select(o=>o.GetType().Name).ToList();var nonVisibleCount=g==null?0:FamilyBuilder.Solids(g).Count();
        var joins=JoinGeometryUtils.GetJoinedElements(doc,pile).ToList();var recovered=0;
        using(var t=new Transaction(doc,"H-PILE 결합 원인 진단 후 되돌림")){t.Start();foreach(var id in joins)JoinGeometryUtils.UnjoinGeometry(doc,pile,doc.GetElement(id));doc.Regenerate();var recoveredGeometry=pile.get_Geometry(new Options{DetailLevel=ViewDetailLevel.Fine});recovered=recoveredGeometry==null?0:FamilyBuilder.Solids(recoveredGeometry).Count();t.RollBack();}
        var details=new{Id=pile.Id.Value,Family=pile.Symbol.FamilyName,Type=pile.Name,Info=Storage.Info(pile),GeometryTypes=geometryTypes,NonVisibleSolids=nonVisibleCount,JoinedElements=joins.Select(id=>new{Id=id.Value,Name=doc.GetElement(id).Name}).ToList(),UnjoinRecoveredSolidCount=recovered,Instance=Params(pile),Symbol=Params(pile.Symbol)};
        File.WriteAllText(Path.Combine(config.Output,"invalid-member-detail.json"),JsonSerializer.Serialize(details,BoreholeSet.JsonOptions));
    }
    private void ApplyHeights()
    {
        var sheet=app.ActiveUIDocument.ActiveView as ViewSheet??throw new InvalidDataException("검증 시트로 전환되지 않았습니다.");
        var vps=sheet.GetAllViewports().Select(id=>(Viewport)doc.GetElement(id)).ToList();Check(vps.Count==route.Count-1,"시트 실제 뷰포트 수 = 경로 구간 수");
        var ordered=vps.OrderBy(v=>((View)doc.GetElement(v.ViewId)).Name).ToList();XYZ? previous=null;
        for(var i=0;i<ordered.Count;i++)
        {
            var view=(View)doc.GetElement(ordered[i].ViewId);var map=SectionSheetData.ToSheet(view,ordered[i]);var a=ModelBuilder.Xyz(route[i]);var b=ModelBuilder.Xyz(route[i+1]);
            var pa=map.OfPoint(new XYZ(a.X,a.Y,0));var pb=map.OfPoint(new XYZ(b.X,b.Y,0));var qa=new XYZ(pa.X,pa.Y,0);var qb=new XYZ(pb.X,pb.Y,0);
            Check(view.Scale==1&&Math.Abs(qa.DistanceTo(qb)-a.DistanceTo(b))<ModelBuilder.Feet(.002),$"{view.Name}: 1:1 시트 구간 길이 검산");
            if(previous!=null)Check(previous.DistanceTo(qa)<ModelBuilder.Feet(.002),$"{view.Name}: 이전 경로 끝점과 연결");previous=qb;
        }
        var attached=vps.SelectMany(v=>SectionSheetData.Read((View)doc.GetElement(v.ViewId))!.Members).ToList();Check(attached.Count==piles.Count&&attached.Distinct().Count()==piles.Count,"선택된 실제 부재 모두 단면에 중복 없이 1회 연결");
        foreach(var vp in vps){var v=(View)doc.GetElement(vp.ViewId);var visible=new FilteredElementCollector(doc,v.Id).WhereElementIsNotElementType().ToElementIds().ToHashSet();Check(SectionSheetData.Read(v)!.Members.All(uid=>visible.Contains(doc.GetElement(uid).Id)),v.Name+": 연결 부재가 실제 단면 표시 대상에 포함");}
        var desiredTop=piles.Min(p=>FamilyBuilder.Bounds(p).Max.Z*.3048+settings.Frame.Elevation)-.25;var maxBottom=piles.Max(p=>FamilyBuilder.Bounds(p).Min.Z*.3048+settings.Frame.Elevation);if(desiredTop<=maxBottom+.5)throw new InvalidDataException("원본 부재 상하단이 뒤섞여 높이 시험 기준을 만들 수 없습니다.");
        var lines=new List<ElementId>();
        using(var t=new Transaction(doc,"시트 높이 기준 상세선 검증"))
        {
            t.Start();foreach(var vp in vps)
            {
                var view=(View)doc.GetElement(vp.ViewId);var map=SectionSheetData.ToSheet(view,vp);var box=view.CropBox;var t0=box.Transform;var z=ModelBuilder.Feet(desiredTop-settings.Frame.Elevation);
                XYZ World(double x){var p=t0.OfPoint(new XYZ(x,0,0));return new(p.X,p.Y,z);}
                var a=map.OfPoint(World(box.Min.X));var b=map.OfPoint(World(box.Max.X));a=new(a.X,a.Y,0);b=new(b.X,b.Y,0);lines.Add(doc.Create.NewDetailCurve(sheet,Line.CreateBound(a,b)).Id);
            }t.Commit();
        }
        var original=piles.ToDictionary(p=>p.UniqueId,p=>FamilyBuilder.Bounds(p).Min.Z*.3048+settings.Frame.Elevation);section.ApplyCheck(app,sheet.Id,lines,true);
        Check(piles.All(p=>Math.Abs(Storage.Info(p)!.Top-desiredTop)<.002&&Math.Abs(Storage.Info(p)!.Bottom-original[p.UniqueId])<.002),"시트 상세선까지 실제 부재 상단 적용 / 현재 실제 하단 유지");
        Check(piles.All(p=>Math.Abs(FamilyBuilder.Bounds(p).Max.Z*.3048+settings.Frame.Elevation-desiredTop)<.002),"실제 Native 부재 솔리드 상단 EL 검산");
        BottomHeights(sheet,vps);
        RevisionRunner.Export(doc,sheet,Path.Combine(config.Output,"02-경로단면시트"));section.Close();Later(HorizontalChecks);
        var detail=vps.Select(v=>(View)doc.GetElement(v.ViewId)).MaxBy(v=>SectionSheetData.Read(v)!.Members.Count)!;RevisionRunner.Export(doc,detail,Path.Combine(config.Output,"03-수직부재단면확대"));
    }
    private void BottomHeights(ViewSheet sheet,List<Viewport> viewports)
    {
        var lines=new List<ElementId>();var wanted=new Dictionary<string,double>();var oldTops=piles.ToDictionary(p=>p.UniqueId,p=>FamilyBuilder.Bounds(p).Max.Z*.3048+settings.Frame.Elevation);double station=0;
        using(var t=new Transaction(doc,"경사진 하단 기준선 시험"))
        {
            t.Start();foreach(var vp in viewports.OrderBy(v=>((View)doc.GetElement(v.ViewId)).Name))
            {
                var view=(View)doc.GetElement(vp.ViewId);var index=int.Parse(view.Name.Split('-')[^1])-1;var start=ModelBuilder.Xyz(route[index]);var direction=(ModelBuilder.Xyz(route[index+1])-start).Normalize();var segmentLength=route[index].DistanceXY(route[index+1]);var map=SectionSheetData.ToSheet(view,vp);var box=view.CropBox;
                double Height(XYZ p)=>-16+.005*(station+(p-start).DotProduct(direction)*.3048);
                XYZ At(double x){var p=box.Transform.OfPoint(new XYZ(x,0,0));var q=map.OfPoint(new XYZ(p.X,p.Y,ModelBuilder.Feet(Height(p)-settings.Frame.Elevation)));return new XYZ(q.X,q.Y,0);}
                lines.Add(doc.Create.NewDetailCurve(sheet,Line.CreateBound(At(box.Min.X),At(box.Max.X))).Id);
                foreach(var uid in SectionSheetData.Read(view)!.Members){var pile=(FamilyInstance)doc.GetElement(uid);var b=FamilyBuilder.Bounds(pile);wanted[uid]=Height((b.Min+b.Max)/2);}station+=segmentLength;
            }t.Commit();
        }
        section.ApplyCheck(app,sheet.Id,lines,false);
        Check(piles.All(p=>Math.Abs(FamilyBuilder.Bounds(p).Min.Z*.3048+settings.Frame.Elevation-wanted[p.UniqueId])<.002&&Math.Abs(FamilyBuilder.Bounds(p).Max.Z*.3048+settings.Frame.Elevation-oldTops[p.UniqueId])<.002),"전체 CIP/H-PILE을 경사진 하단선에 맞춤 / 실제 상단 유지 (2mm 이내)");
    }
    private void HorizontalChecks()
    {
        var sheet=(ViewSheet)app.ActiveUIDocument.ActiveView;var vp=(Viewport)doc.GetElement(sheet.GetAllViewports().First());var view=(View)doc.GetElement(vp.ViewId);var map=SectionSheetData.ToSheet(view,vp);var box=view.CropBox;var guides=new List<ElementId>();var expected=new[]{8.0,3.0,-2.0};
        using(var t=new Transaction(doc,"시험용 3단 중심높이선"))
        {
            t.Start();foreach(var el in expected){XYZ At(double x){var p=box.Transform.OfPoint(new XYZ(x,0,0));var q=map.OfPoint(new XYZ(p.X,p.Y,ModelBuilder.Feet(el-settings.Frame.Elevation)));return new XYZ(q.X,q.Y,0);}guides.Add(doc.Create.NewDetailCurve(sheet,Line.CreateBound(At(box.Min.X),At(box.Max.X))).Id);}t.Commit();
        }
        var levels=SheetTierHeights.Read(doc,sheet,guides,settings.Frame.Elevation);Check(levels.Zip(expected).All(p=>Math.Abs(p.First-p.Second)<.002)&&levels.Count==3,"실제 시트 상세선에서 EL 8/3/-2m 3단 높이 추출");
        var choices=ScreenChoices.Families(doc,ScreenChoices.Horizontal);File.WriteAllText(Path.Combine(config.Output,"horizontal-families.json"),JsonSerializer.Serialize(choices,BoreholeSet.JsonOptions));
        var selected=choices.FirstOrDefault(c=>c.Id==settings.StrutFamily)??choices.FirstOrDefault(c=>System.Text.RegularExpressions.Regex.IsMatch(c.Label,"STRUT|WALER|띠장|버팀|스트럿",System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        if(selected==null)
        {
            var file=PlacementChecks.CreateFamily(app,true,config.Output);using var t=new Transaction(doc,"수평 검증용 패밀리 로드");t.Start();doc.LoadFamily(file,out var family);var symbol=(FamilySymbol)doc.GetElement(family.GetFamilySymbolIds().First());selected=new(symbol.UniqueId,symbol.FamilyName);t.Commit();log.Add("로드된 수평 패밀리 없음: 생성한 시험용 STRUT RFA 사용, 사용자 패밀리 호환성은 별도");
        }
        else log.Add("프로젝트 수평 패밀리 사용: "+selected.Label);
        var paths=new List<PlanSegment>();
        foreach(var cad in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
        {
            var reader=CadReader.Read(doc,cad);var groups=reader.Paths.Where(p=>System.Text.RegularExpressions.Regex.IsMatch(p.Layer,"STRUT|스트럿|버팀|띠장",System.Text.RegularExpressions.RegexOptions.IgnoreCase)).GroupBy(p=>p.Layer);
            foreach(var g in groups){var found=HorizontalRecognition.Centerlines(g.Select(p=>new PlanPath(p.Points.Select(ModelBuilder.Meters).ToList(),p.Closed))).Where(p=>p.Length>1&&p.Length<100).Take(3).ToList();if(found.Count>0){paths=found;log.Add("수평 시험 실제 CAD 레이어: "+g.Key);break;}}if(paths.Count>0)break;
        }
        if(paths.Count==0){paths=[new(route[0],route[1]),new(route[1],route[2])];log.Add("실제 CAD STRUT 레이어 자동확정 불가: 실제 경로 2구간을 시험 배치 중심선으로만 사용");}
        List<FamilyInstance> beams;using(var t=new Transaction(doc,"시트 높이 3단 Native 수평부재 시험")){t.Start();beams=HorizontalService.Place(doc,paths,levels,selected.Id,settings,"USER-NATIVE-HORIZONTAL");t.Commit();}
        Check(beams.Count==paths.Count*3,$"시트 기준 수평부재 {paths.Count}개×3단={beams.Count}개 실제 배치");
        foreach(var beam in beams){var b=FamilyBuilder.Bounds(beam);var el=(b.Min.Z+b.Max.Z)/2*.3048+settings.Frame.Elevation;Check(Math.Abs(el-Storage.Info(beam)!.Top)<.002,$"수평부재 {beam.Id} 실제 솔리드 중심 높이 EL {el:F3}m 일치");}
        doc.SaveAs(Path.Combine(config.Output,"CivilBIM-사용자료-검증.rvt"),new SaveAsOptions {OverwriteExistingFile=true});Report("passed");
    }
    private static IEnumerable<T> Controls<T>(System.Windows.DependencyObject root)where T:System.Windows.DependencyObject
    {if(root is T result)yield return result;for(var i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Controls<T>(System.Windows.Media.VisualTreeHelper.GetChild(root,i)))yield return child;}
}
