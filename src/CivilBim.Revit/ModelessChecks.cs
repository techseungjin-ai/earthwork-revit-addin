using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using CivilBim.Core;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ComboBox=System.Windows.Controls.ComboBox;
using TextBox=System.Windows.Controls.TextBox;

namespace CivilBim.Revit;

public sealed class ModelessCheckStartup:IExternalApplication
{
    public Result OnStartup(UIControlledApplication app)
    {
        var config=Environment.GetEnvironmentVariable("CIVILBIM_UI_CONFIG");if(string.IsNullOrWhiteSpace(config))return Result.Succeeded;
        app.ControlledApplication.FailuresProcessing+=(_,args)=>{var a=args.GetFailuresAccessor();foreach(var f in a.GetFailureMessages().Where(f=>f.GetSeverity()==FailureSeverity.Warning))a.DeleteWarning(f);};
        EventHandler<IdlingEventArgs>? start=null;start=(sender,_)=>{app.Idling-=start;if(sender is UIApplication ui)new ModelessChecks(ui,config).Start();};app.Idling+=start;return Result.Succeeded;
    }
    public Result OnShutdown(UIControlledApplication app)=>Result.Succeeded;
}
public sealed class ModelessChecks
{
    public sealed class Config {public string Output {get;set;}="";public string Pdf {get;set;}="";}
    private readonly UIApplication app;
    private readonly Config config;
    private readonly List<string> log=[];
    private readonly List<ModelessWindow> windows=[];
    private Document doc=null!;
    private Settings settings=null!;
    private SoilWindow soil=null!;
    private VerticalWindow vertical=null!;
    private ModelessWindow otherSession=null!;
    private string firstFile="",otherFile="";
    private bool finished;
    private readonly List<(Func<View,bool> Matches,Action Next)> viewWaits=[];
    private EventHandler<ViewActivatedEventArgs>? viewHandler;
    private DateTime started=DateTime.UtcNow;
    public ModelessChecks(UIApplication a,string file){app=a;config=JsonSerializer.Deserialize<Config>(File.ReadAllText(file))!;}
    private void Report(string status,string stage,Exception? error=null)=>File.WriteAllText(Path.Combine(config.Output,"result.json"),JsonSerializer.Serialize(new{Status=status,Stage=stage,Log=log,Error=error?.ToString()},BoreholeSet.JsonOptions));
    private void Fail(Exception error){if(finished)return;finished=true;Report("failed","failed",error);foreach(var w in windows.ToList())if(!w.WasClosed)w.Close();}
    private void Later(Action action)=>Dispatcher.CurrentDispatcher.BeginInvoke(new Action(()=>{try{action();}catch(Exception ex){Fail(ex);}}),DispatcherPriority.Background);
    private void Check(bool yes,string text){if(!yes)throw new InvalidDataException(text);log.Add(text);}
    private T Open<T>(string key,Func<T> create) where T:ModelessWindow=>ModelessHost.Open(app,key,create,w=>{w.ShowActivated=false;w.WindowStartupLocation=WindowStartupLocation.Manual;w.Left=-10000;w.Top=-10000;w.Failed+=Fail;windows.Add(w);});
    public void Start()
    {
        try
        {
            Report("running","prepare");started=DateTime.UtcNow;
            viewHandler=(_,_)=>{try{if(finished)return;var active=app.ActiveUIDocument?.ActiveView;if(active==null)return;foreach(var wait in viewWaits.ToList())if(wait.Matches(active)){viewWaits.Remove(wait);Later(wait.Next);}}catch(Exception ex){Fail(ex);}};app.ViewActivated+=viewHandler;
            var data=BoreholePdf.ExtractCandidates(config.Pdf,"EPSG:5186");
            using(var d=app.Application.NewProjectDocument(@"C:\ProgramData\Autodesk\RVT 2027\Templates\Default_M_KOR.rte"))
            {
                settings=new Settings {Frame=new("EPSG:5186",data.Boreholes.Average(h=>h.Easting),data.Boreholes.Average(h=>h.Northing),0,37)};
                using var t=new Transaction(d,"UI 검증용 단순 지형 — 실제 지형 아님");t.Start();var type=new FilteredElementCollector(d).OfClass(typeof(ToposolidType)).FirstElementId();var level=new FilteredElementCollector(d).OfClass(typeof(Level)).Cast<Level>().OrderBy(l=>Math.Abs(l.Elevation)).First();
                var points=new[]{new Point3(-250,-250,15),new Point3(250,-250,15),new Point3(250,250,15),new Point3(-250,250,15),new Point3(0,0,15)};
                var terrain=Toposolid.Create(d,points.Select(ModelBuilder.Xyz).ToList(),type,level.Id);Storage.Write(terrain,new ElementInfo("Terrain","UI-FIXTURE","지형",15,15,0,0,0,"","UI-FIXTURE","UI-FIXTURE",new(),settings.Frame,1000));Storage.Write(d.ProjectInformation,settings);t.Commit();
                firstFile=Path.Combine(config.Output,"CBIM-UI04-Fixture.rvt");d.SaveAs(firstFile,new SaveAsOptions {OverwriteExistingFile=true});d.Close(false);
            }
            doc=app.OpenAndActivateDocument(firstFile).Document;
            using(var other=app.Application.NewProjectDocument(@"C:\ProgramData\Autodesk\RVT 2027\Templates\Default_M_KOR.rte")){otherFile=Path.Combine(config.Output,"CBIM-UI04-Other.rvt");other.SaveAs(otherFile,new SaveAsOptions {OverwriteExistingFile=true});other.Close(false);}
            var view=Views3D();app.ActiveUIDocument.RequestViewChange(view);
            soil=Open("UI-test-soil",()=>new SoilWindow(doc,settings));vertical=Open("UI-test-CIP",()=>new VerticalWindow(doc,settings,"CIP"));
            soil.Session!.Trace+=s=>{log.Add(s);Report("running","soil-event");};
            Open("UI-test-HPILE",()=>new VerticalWindow(doc,settings,"H-PILE"));Open("UI-test-horizontal",()=>new HorizontalWindow(doc,settings));Open("UI-test-context",()=>new ContextWindow(doc,settings));Open("UI-test-workflow",()=>new WorkflowWindow(settings,[],FamilyBuilder.Choices(doc)));
            var review=Open("UI-test-review",()=>new ReviewWindow(data));var reviewed=review.BuildReviewed(true);Check(reviewed.Boreholes.Count==5,"PDF 추출 편집 표에서 BH5공/지층 검토 자료 구성");
            var json=Path.Combine(config.Output,"reviewed-test.json");File.WriteAllText(json,JsonSerializer.Serialize(reviewed,BoreholeSet.JsonOptions));soil.Input.Use(json);
            Export(review,"02-시추PDF-검토표.png");Export(soil,"01-작업대지-모델리스.png");Export(vertical,"03-CIP-화면선택.png");
            OnView(v=>v.Id==view.Id,()=>
            {
                Check(!ComponentDispatcher.IsThreadModal&&windows.All(w=>w.IsVisible),"기능창7개 동시 표시 / WPF 모달 루프 없음");
                Check(Tree(soil).OfType<TextBox>().All(t=>t.Name!="BottomEl"),"생성 화면에서 굴착 EL 입력 제거");
                OnView(v=>v is ViewPlan,()=>{Check(soil.IsVisible,"모델리스 창을 유지한 채 ExternalEvent로 3D→평면 뷰 전환");Report("running","plan-ok");ImportCad();});
                soil.Api(a=>a.ActiveUIDocument.RequestViewChange(new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(v=>!v.IsTemplate&&v.ViewType==ViewType.FloorPlan)));
                Report("running","plan-button-clicked");
            });
            var timeout=new DispatcherTimer {Interval=TimeSpan.FromSeconds(1)};timeout.Tick+=(_,_)=>{if(finished){timeout.Stop();return;}if(DateTime.UtcNow-started>TimeSpan.FromMinutes(3)){timeout.Stop();log.Add("Soil status: "+soil.StatusText);Fail(new TimeoutException("모델리스 ExternalEvent 검증 제한시간 초과."));}};timeout.Start();
        }catch(Exception ex){Fail(ex);}
    }
    private View3D Views3D()
    {
        using var t=new Transaction(doc,"UI 검증 뷰");t.Start();var view=Views.Create(doc,"UI검증_3D",new[]{new Point3(-50,-50,0),new Point3(50,50,15)});t.Commit();return view;
    }
    private void OnView(Func<View,bool> matches,Action action)
    {
        viewWaits.Add((matches,action));
    }
    private void ImportCad()
    {
        soil.Api(_=>
        {
            var file=Path.Combine(config.Output,"UI04-Plan.dxf");
            File.WriteAllText(file,"0\nSECTION\n2\nHEADER\n9\n$ACADVER\n1\nAC1009\n0\nENDSEC\n0\nSECTION\n2\nTABLES\n0\nTABLE\n2\nLAYER\n70\n3\n0\nLAYER\n2\n0\n70\n0\n62\n7\n6\nCONTINUOUS\n0\nLAYER\n2\nCIP\n70\n0\n62\n7\n6\nCONTINUOUS\n0\nLAYER\n2\nH-PILE\n70\n0\n62\n3\n6\nCONTINUOUS\n0\nENDTAB\n0\nENDSEC\n0\nSECTION\n2\nENTITIES\n0\nCIRCLE\n8\nCIP\n10\n0\n20\n0\n30\n0\n40\n0.265\n0\nLINE\n8\nH-PILE\n10\n9.85\n20\n-0.1\n30\n0\n11\n10.15\n21\n-0.1\n31\n0\n0\nLINE\n8\nH-PILE\n10\n9.85\n20\n0.1\n30\n0\n11\n10.15\n21\n0.1\n31\n0\n0\nLINE\n8\nH-PILE\n10\n10\n20\n-0.1\n30\n0\n11\n10\n21\n0.1\n31\n0\n0\nENDSEC\n0\nEOF\n",System.Text.Encoding.ASCII);
            using var t=new Transaction(doc,"창 유지 상태 CAD 추가 검증");t.Start();var options=new DWGImportOptions {Unit=ImportUnit.Meter,Placement=ImportPlacement.Origin,ThisViewOnly=false};if(!doc.Import(file,options,app.ActiveUIDocument.ActiveView,out var id))throw new InvalidDataException("검증 DXF 가져오기 실패.");t.Commit();
            var reader=CadReader.Read(doc,(ImportInstance)doc.GetElement(id));Check(reader.Circles.Count==1&&reader.HPiles("H-PILE").Count==1,"새 CAD의 CIP 원/H형 선군과 레이어 읽기");var circle=reader.Circles[0];Check(ScreenChoices.NearestLayer(reader,circle.Center+XYZ.BasisX.Multiply(circle.Radius))=="CIP","CAD 선 선택의 최근접 레이어 판별");Later(RefreshCad);
        });
    }
    private void RefreshCad()
    {
        Button(vertical,"새로고침").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        vertical.Api(_=>Later(()=>
        {
            var combo=Tree(vertical).OfType<ComboBox>().First(b=>b.ItemsSource is IEnumerable<CadChoice>);Check(combo.Items.Count==1,"창을 연 뒤 추가한 CAD가 새로고침 드롭다운에 반영");combo.SelectedIndex=0;
            vertical.Api(_=>{Check(vertical.Reader?.Layers.Contains("CIP")==true,"모델리스 CAD 드롭다운 선택 후 ExternalEvent 레이어 갱신");Export(vertical,"03-CIP-화면선택.png");Later(Markers);});
        }));
    }
    private void Markers()
    {
        Button(soil,"시추공 좌표 위치 보기").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        soil.Api(_=>
        {
            var markers=Storage.Generated(doc).Where(x=>x.Info.Kind=="BoreholeMarker").ToList();var data=soil.Input.Data();
            Check(markers.Count==data.Boreholes.Count,"37도 회전 대지의 시추공 위치 표시 생성");
            foreach(var marker in markers)
            {
                var hole=data.Boreholes.Single(h=>h.Id==marker.Info.ParentId);var expected=settings.Frame.ToLocal(new(hole.Easting,hole.Northing,hole.GroundElevation));var box=marker.Element.get_BoundingBox(null);var center=ModelBuilder.Meters((box.Min+box.Max)*.5);
                Check(center.DistanceXY(expected)<.001&&Math.Abs(box.Min.Z*.3048-expected.Z)<.001,"시추공 "+hole.Id+" 실제 모델 중심/표고 = 저장한 공통 회전 좌표");
            }
            Later(Boundary);
        });
    }
    private void Boundary()
    {
        var points=soil.Input.Data().Boreholes.Select(b=>settings.Frame.ToLocal(new(b.Easting,b.Northing))).Select(p=>new NetTopologySuite.Geometries.Coordinate(p.X,p.Y)).ToArray();soil.SetBoundaryForCheck(Polygons.Factory.CreateMultiPointFromCoords(points).ConvexHull().Buffer(3));
        soil.Api(_=>{Check(soil.Boundary?.Area>0,"시추공 좌표 기반 임시 경계 생성과 예시 표시");Later(Excavation);});
    }
    private void Excavation()
    {
        soil.Api(_=>{Check(soil.IsVisible&&Storage.Generated(doc).All(x=>x.Info.Kind!="SiteSoil"),"단순화 창에서 지층 생성 전 모델 변경 없음");Later(Generate);});
    }
    private void Generate()
    {
        OnView(v=>v.Name.StartsWith("02_작업대지"),()=>soil.Api(_=>{app.OpenAndActivateDocument(otherFile);otherSession=Open("UI-test-other",()=>new ModelessWindow {Title="UI 검증 문서 격리",Width=300,Height=200,Content=new TextBlock {Text="검증용"}});Later(WrongDocument);}));
        Button(soil,"시추주상도 기반 지층 생성").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        soil.Api(_=>
        {
            Check(Storage.Generated(doc).Count(x=>x.Info.Kind=="SiteSoil")>=3&&soil.IsVisible,"모델리스 생성 버튼으로 실제 지층/일람표 작성 후 창 유지");var saved=Storage.Load(doc);Check(!saved.HasExcavationElevation&&saved.BoreholeJson.Length>0&&saved.WorksiteBoundaryWkt.Length>0,"굴착EL 없이 시추 경로·생성범위 저장");
            var p=doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);Check(Math.Abs(p.EastWest)+Math.Abs(p.NorthSouth)+Math.Abs(p.Elevation)<1e-7,"공유좌표 자동 설정 없음");Export(soil,"04-생성완료-창유지.png");doc.Save();
        });
    }
    private void WrongDocument()
    {
        var ran=false;soil.Api(_=>ran=true);
        Later(()=>WaitWrongDocument(()=>
        {
            Check(!ran&&soil.StatusText.Contains("다른 프로젝트"),"다른 프로젝트 활성화 시 외부 이벤트 변경 차단");
            otherSession.Api(_=>{app.OpenAndActivateDocument(firstFile);Later(Cancel);});
        }));
    }
    private void WaitWrongDocument(Action done)
    {
        if(soil.StatusText.Contains("다른 프로젝트")){done();return;}var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(200)};timer.Tick+=(_,_)=>{if(soil.StatusText.Contains("다른 프로젝트")){timer.Stop();try{done();}catch(Exception ex){Fail(ex);}}else if(finished)timer.Stop();};timer.Start();
    }
    private void Cancel()
    {
        soil.Api(_=>throw new System.OperationCanceledException("검증용 선택 취소"));
        var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(200)};timer.Tick+=(_,_)=>
        {
            if(finished){timer.Stop();return;}if(!soil.StatusText.Contains("취소"))return;timer.Stop();
            soil.Api(_=>{Check(soil.IsVisible,"선택 취소 후 창/입력 유지 및 다음 요청 재실행");finished=true;if(viewHandler!=null)app.ViewActivated-=viewHandler;Report("passed","done");foreach(var w in windows.ToList())w.Close();});
        };timer.Start();
    }
    private static IEnumerable<FrameworkElement> Tree(DependencyObject parent)
    {
        if(parent is FrameworkElement e)yield return e;foreach(var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())foreach(var item in Tree(child))yield return item;
    }
    private static T Find<T>(Window window,string name) where T:FrameworkElement=>Tree(window).OfType<T>().First(e=>e.Name==name);
    private static System.Windows.Controls.Button Button(Window w,string text)=>Tree(w).OfType<System.Windows.Controls.Button>().First(b=>Equals(b.Content,text));
    private void Export(Window w,string name)
    {
        var content=(FrameworkElement)w.Content;var size=new System.Windows.Size(w.Width,w.Height-45);content.Measure(size);content.Arrange(new Rect(size));content.UpdateLayout();
        var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);bitmap.Render(content);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(config.Output,name));png.Save(file);
    }
}


