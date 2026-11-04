using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using CivilBim.Core;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ComboBox=System.Windows.Controls.ComboBox;

namespace CivilBim.Revit;

public sealed class LocalWorkflowCheckStartup:IExternalApplication
{
    public Result OnStartup(UIControlledApplication app)
    {
        var output=Environment.GetEnvironmentVariable("CIVILBIM_LOCAL_CHECKS");if(string.IsNullOrWhiteSpace(output))return Result.Succeeded;
        app.ControlledApplication.FailuresProcessing+=(_,e)=>{var a=e.GetFailuresAccessor();foreach(var f in a.GetFailureMessages().Where(f=>f.GetSeverity()==FailureSeverity.Warning))a.DeleteWarning(f);};
        EventHandler<IdlingEventArgs>? start=null;start=(sender,_)=>{app.Idling-=start;if(sender is UIApplication ui)new LocalWorkflowChecks(ui,output).Start();};app.Idling+=start;return Result.Succeeded;
    }
    public Result OnShutdown(UIControlledApplication app)=>Result.Succeeded;
}
public sealed class LocalWorkflowChecks
{
    private readonly UIApplication app;private readonly string output;private readonly List<string> log=[];private readonly List<ModelessWindow> windows=[];
    private Document doc=null!;private Settings settings=null!;private List<FamilyInstance> piles=[];private FamilySymbol horizontal=null!;
    public LocalWorkflowChecks(UIApplication ui,string directory){app=ui;output=directory;}
    private void Report(string status,Exception? ex=null)=>File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new {Status=status,Log=log,Error=ex?.ToString()},BoreholeSet.JsonOptions));
    private void Check(bool ok,string message){if(!ok)throw new InvalidDataException(message);log.Add(message);Report("running");}
    private void Fail(Exception ex){Report("failed",ex);foreach(var w in windows.ToList())if(!w.WasClosed)w.Close();}
    private T Open<T>(string key,Func<T> create,Action<T>? setup=null)where T:ModelessWindow=>ModelessHost.Open(app,key,create,w=>{w.ShowActivated=false;w.WindowStartupLocation=WindowStartupLocation.Manual;w.Left=-10000;w.Top=-10000;w.Failed+=Fail;windows.Add(w);setup?.Invoke(w);});
    public void Start()
    {
        try
        {
            Report("running");doc=app.Application.NewProjectDocument(@"C:\ProgramData\Autodesk\RVT 2027\Templates\Default_M_KOR.rte");settings=new Settings {Frame=new("TEST",200000,500000),CipLengthParameter="Depth",CipDiameterParameter="DO_NOT_WRITE",Diameter=.99};
            Toposolid terrain,road;DirectShape marker;ModelCurve north;
            using(var t=new Transaction(doc,"로컬 검증용 대지/도로/시추 위치"))
            {
                t.Start();var type=new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).FirstElementId();var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstElementId();terrain=Toposolid.Create(doc,new[]{new Point3(-20,-20,10),new(20,-20,10),new(20,20,10),new(-20,20,10)}.Select(ModelBuilder.Xyz).ToList(),type,level);
                Storage.Write(terrain,Info("Terrain",new()));var polygon=Polygons.Factory.CreatePolygon(new[]{new NetTopologySuite.Geometries.Coordinate(-10,-10),new(-5,-10),new(-5,-5),new(-10,-5),new(-10,-10)});road=terrain.CreateSubDivision(doc,type,ContextBuilder.Loops(polygon,0,doc.Application.ShortCurveTolerance));road.get_Parameter(BuiltInParameter.TOPOSOLID_SUBDIVIDE_HEIGHT).Set(ModelBuilder.Feet(.15));Storage.Write(road,Info("Road",new()));
                marker=ModelBuilder.Shape(doc,ModelBuilder.Cylinder(ModelBuilder.Xyz(new(5,6,10)),10,11,.6,ElementId.InvalidElementId),Info("BoreholeMarker",settings.Frame.ToSurvey(new(5,6,10))));
                var plane=SketchPlane.Create(doc,Plane.CreateByNormalAndOrigin(XYZ.BasisZ,XYZ.Zero));north=doc.Create.NewModelCurve(Line.CreateBound(ModelBuilder.Xyz(new(3,4)),ModelBuilder.Xyz(new(3,14))),plane);Storage.Write(doc.ProjectInformation,settings);t.Commit();
            }
            var oldRoad=ContextBuilder.Surface(road).Triangles.Select(t=>(NetTopologySuite.Geometries.Geometry)t.Polygon).ToList();var footprint=NetTopologySuite.Operation.Union.UnaryUnionOp.Union(oldRoad);
            var a=37*Math.PI/180;var angle=DrawingSelections.NorthAngle([new(3,4),new(3,14)],[new(3,4),new(3-10*Math.Sin(a),4+10*Math.Cos(a))]);Check(Math.Abs(angle-37)<1e-8,"북방향 두 선에서 +37도 계산");
            SiteAlignment.Rotate(doc,new(3,4),angle,north.Id);settings=Storage.Load(doc);
            var expected=settings.Frame.ToLocal(new(200005,500006,10));var box=FamilyBuilder.Bounds(marker);Check(ModelBuilder.Meters((box.Min+box.Max)*.5).DistanceXY(expected)<.001,"임의 회전 중심: 실제 시추 표시와 저장 Frame 정합");
            var expectedRoad=NetTopologySuite.Geometries.Utilities.AffineTransformation.RotationInstance(a,3,4).Transform(footprint);var actualRoad=NetTopologySuite.Operation.Union.UnaryUnionOp.Union(ContextBuilder.Surface(road).Triangles.Select(t=>(NetTopologySuite.Geometries.Geometry)t.Polygon).ToList());Check(expectedRoad.SymmetricDifference(actualRoad).Area<.01,"원본 지형 회전 시 도로 구획도 함께 회전 / 면적 유지");
            var project=doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);Check(Math.Abs(project.Angle)+Math.Abs(project.EastWest)+Math.Abs(project.NorthSouth)<1e-8,"공유좌표/진북 설정 변경 없음");
            var cipFile=CreateCip();var horizontalFile=PlacementChecks.CreateFamily(app,true,output);
            using(var t=new Transaction(doc,"로컬 검증용 고객 대체 패밀리"))
            {
                t.Start();doc.LoadFamily(cipFile,out var family);settings.CipFamily=doc.GetElement(family.GetFamilySymbolIds().First()).UniqueId;doc.LoadFamily(horizontalFile,out var hf);horizontal=(FamilySymbol)doc.GetElement(hf.GetFamilySymbolIds().First());
                foreach(var (p,i) in new[]{new Point3(5,0),new Point3(10,5)}.Select((p,i)=>(p,i)))piles.Add(ModelBuilder.Pile(doc,ModelBuilder.Xyz(p),new("LOCAL-CIP-"+i,"미분할",15,-15,.99,""),settings,"LOCAL-CIP","LOCAL-CIP"));
                Storage.Write(doc.ProjectInformation,settings);var plan=new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(v=>!v.IsTemplate&&v.ViewType==ViewType.FloorPlan);var crop=plan.CropBox;crop.Min=ModelBuilder.Xyz(new(-200,-200,-100));crop.Max=ModelBuilder.Xyz(new(200,200,100));plan.CropBox=crop;plan.CropBoxActive=true;StartingViewSettings.GetStartingViewSettings(doc).ViewId=plan.Id;t.Commit();
            }
            Check(piles.All(p=>Math.Abs(Storage.Info(p)!.Diameter-.53)<1e-6&&Storage.Info(p)!.DiameterParameter==""),"CIP 요청직경 .99 무시 / 선택유형 실제 .53 유지 / 직경 파라미터 쓰기 없음");
            var file=Path.Combine(output,"CBIM-LocalWorkflow.rvt");var horizontalId=horizontal.UniqueId;doc.SaveAs(file,new SaveAsOptions {OverwriteExistingFile=true});doc.Close(false);doc=app.OpenAndActivateDocument(file).Document;piles=Storage.Generated(doc).Where(x=>x.Info.Source=="LOCAL-CIP").Select(x=>(FamilyInstance)x.Element).ToList();horizontal=(FamilySymbol)doc.GetElement(horizontalId);
            ImportDrawing();Profile();
        }catch(Exception ex){Fail(ex);}
    }
    private ElementInfo Info(string kind,Point3 position)=>new(kind,"LOCAL","검증",10,0,0,0,0,"","LOCAL","LOCAL",position,settings.Frame,1000);
    private void Profile()
    {
        var window=Open("로컬깊이",()=>new DrawingDevelopmentWindow(doc,piles.Select(p=>p.Id)));window.SetProfileMode(0);window.Alignment=[new(0,0),new(10,0),new(10,10)];window.Upper=[new(new(100,30,12),new(120,30,10))];window.Lower=[new(new(100,30,-8),new(120,30,-12))];
        Button(window,"깊이 인식 / 결과 검토").RaiseEvent(new RoutedEventArgs(ButtonBaseClick()));
        window.Api(_=>
        {
            Check(piles.All(p=>Storage.Info(p)!.Top==15),"전개도 결과 검토는 모델을 변경하지 않음");Button(window,"선택한 CIP / H-PILE 깊이를 모델에 적용").RaiseEvent(new RoutedEventArgs(ButtonBaseClick()));
            Check(piles.All(p=>Math.Abs(Storage.Info(p)!.Top-(Storage.Info(p)!.ParentId.EndsWith("0")?11.5:10.5))<1e-6),"실제 적용 버튼: 꺾인 경로 누적거리5/15m → 실제 Z 상단11.5/10.5");
            Check(piles.All(p=>Math.Abs(FamilyBuilder.Bounds(p).Min.Z*.3048-Storage.Info(p)!.Bottom)<.002),"깊이 적용 후 실제 패밀리 하단 Z 검사");Render(window,"01-깊이-검토-적용.png");window.Close();Horizontal();
        });
    }
    private void Horizontal()
    {
        var window=Open("로컬수평",()=>new HorizontalWindow(doc,settings),w=>HorizontalCommand.Bind(doc,settings,w));Find<ComboBox>(window,"HorizontalPlanLayer").Text="PLAN";Find<ComboBox>(window,"HorizontalTiers").Text="TIERS";var family=Find<ComboBox>(window,"HorizontalFamily");family.SelectedItem=family.Items.Cast<FamilyChoice>().First(f=>f.Id==horizontal.UniqueId);
        Button(window,"1. 단수 인식 / 배치 결과 검토").RaiseEvent(new RoutedEventArgs(ButtonBaseClick()));window.Api(_=>
        {
            Check(Storage.Generated(doc).All(p=>p.Info.Kind!="Strut")&&window.StatusText.Contains("아직"),"STRUT 검토 버튼은 생성 없이 2개×3단 인식");Button(window,"2. STRUT · 띠장을 모델에 적용").RaiseEvent(new RoutedEventArgs(ButtonBaseClick()));
            var placed=Storage.Generated(doc).Where(p=>p.Info.Kind=="Strut").ToList();Check(placed.Count==6&&placed.Select(p=>Math.Round((FamilyBuilder.Bounds(p.Element).Min.Z+FamilyBuilder.Bounds(p.Element).Max.Z)*.3048/2,3)).Distinct().OrderDescending().SequenceEqual(new[]{8.0,3.0,-2.0}),"STRUT 실제 적용 버튼 → 6개 생성 / 실제 Z8,3,-2");Render(window,"02-STRUT-검토-적용.png");window.Close();
            var cip=Open("로컬CIP",()=>new VerticalWindow(doc,settings,"CIP"));Render(cip,"03-CIP-유형치수.png");cip.Close();var alignment=Open("로컬방향",()=>new SiteAlignmentWindow());Render(alignment,"04-북방향-선-회전.png");alignment.Close();doc.Save();Report("passed");
        });
    }
    private string CreateCip()
    {
        using var family=app.Application.NewFamilyDocument(@"C:\ProgramData\Autodesk\RVT 2027\Family Templates\English\Metric Generic Model.rft");using(var t=new Transaction(family,"고정 유형 D530 fixture")){t.Start();family.FamilyManager.NewType("검증용 D530");var depth=family.FamilyManager.AddParameter("Depth",GroupTypeId.Geometry,SpecTypeId.Length,true);family.FamilyManager.Set(depth,ModelBuilder.Feet(1));var c=new CurveArray();var r=ModelBuilder.Feet(.265);c.Append(Arc.Create(XYZ.Zero,r,0,Math.PI,XYZ.BasisX,XYZ.BasisY));c.Append(Arc.Create(XYZ.Zero,r,Math.PI,2*Math.PI,XYZ.BasisX,XYZ.BasisY));var loops=new CurveArrArray();loops.Append(c);var plane=SketchPlane.Create(family,Plane.CreateByNormalAndOrigin(XYZ.BasisZ,XYZ.Zero));var solid=family.FamilyCreate.NewExtrusion(true,loops,plane,ModelBuilder.Feet(1));family.FamilyManager.AssociateElementParameterToFamilyParameter(solid.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM),depth);t.Commit();}var file=Path.Combine(output,"검증용_CIP_D530.rfa");family.SaveAs(file,new SaveAsOptions {OverwriteExistingFile=true});return file;
    }
    private void ImportDrawing()
    {
        var dxf="0\nSECTION\n2\nHEADER\n9\n$ACADVER\n1\nAC1009\n0\nENDSEC\n0\nSECTION\n2\nENTITIES\n";
        void Line(string layer,double x,double y,double z,double endX,double endY,double endZ)=>dxf+=$"0\nLINE\n8\n{layer}\n10\n{x}\n20\n{y}\n30\n{z}\n11\n{endX}\n21\n{endY}\n31\n{endZ}\n";
        Line("PLAN",-5,-5,0,5,-5,0);Line("PLAN",-5,-2,0,5,-2,0);foreach(var z in new[]{8,3,-2})Line("TIERS",100,30,z,120,30,z);dxf+="0\nENDSEC\n0\nEOF\n";var file=Path.Combine(output,"Local-Plan-Profile.dxf");File.WriteAllText(file,dxf,System.Text.Encoding.ASCII);using var t=new Transaction(doc,"검증 CAD");t.Start();if(!doc.Import(file,new DWGImportOptions {Unit=ImportUnit.Meter,Placement=ImportPlacement.Origin,ThisViewOnly=false},app.ActiveUIDocument.ActiveView,out _))throw new InvalidDataException("검증 CAD 가져오기 실패");t.Commit();
    }
    private static RoutedEvent ButtonBaseClick()=>System.Windows.Controls.Button.ClickEvent;
    private static IEnumerable<FrameworkElement> Tree(DependencyObject p){if(p is FrameworkElement e)yield return e;foreach(var c in LogicalTreeHelper.GetChildren(p).OfType<DependencyObject>())foreach(var item in Tree(c))yield return item;}
    private static T Find<T>(Window w,string name)where T:FrameworkElement=>Tree(w).OfType<T>().First(e=>e.Name==name);
    private static Button Button(Window w,string text)=>Tree(w).OfType<Button>().First(b=>Equals(b.Content,text));
    private void Render(Window w,string file){var content=(FrameworkElement)w.Content;var size=new System.Windows.Size(w.Width,w.Height-45);content.Measure(size);content.Arrange(new Rect(size));content.UpdateLayout();var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);bitmap.Render(content);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(output,file));png.Save(stream);}
}
