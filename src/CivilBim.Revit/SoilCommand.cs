using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CivilBim.Core;
using NtsGeometry=NetTopologySuite.Geometries.Geometry;

namespace CivilBim.Revit;

public sealed class SoilWindow:ModelessWindow
{
    private readonly Document doc;
    private readonly Settings settings;
    public BoreholePicker Input {get;}
    public NtsGeometry? Boundary {get;private set;}
    public SoilWindow(Document document,Settings s)
    {
        doc=document;settings=s;Title="시추주상도 기반 지층 생성";Width=760;MinHeight=420;MaxHeight=Math.Max(420,SystemParameters.WorkArea.Height-40);Height=Math.Min(650,MaxHeight);WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new StackPanel {Margin=new(22)};Content=new ScrollViewer {Content=root,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        root.Children.Add(new TextBlock {Text="PDF 검토 후 시추 기반 지층 생성",FontSize=23,FontWeight=FontWeights.SemiBold});
        root.Children.Add(new TextBlock {Text="1. 시추주상도 PDF 읽기 · 추출표 검토",Margin=new(0,16,0,0)});
        Input=new(this,root,s,standalone:true);Input.Loaded+=()=>SetStatus("시추자료 검토 완료. 아래 ‘시추주상도 기반 지층 생성’을 누르세요.");
        var show=new Button {Content="시추공 좌표 위치 보기",Padding=new(9,6,9,6),HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,8,0,8)};root.Children.Add(show);show.Click+=(_,_)=>Api(app=>ShowHoles(app,Input.Data()));
        var verify=new Button {Content="생성된 지층 검증 (PDF 대조)",Padding=new(9,6,9,6),HorizontalAlignment=HorizontalAlignment.Left};root.Children.Add(verify);verify.Click+=(_,_)=>Api(app=>{var report=SoilValidation.Check(doc,Input.Data());ModelessHost.Open(app,"시추지층검증",()=>new SoilValidationWindow()).Update(report);});
        root.Children.Add(new TextBlock {Text="2. 아래 버튼을 눌러 지층 생성",FontWeight=FontWeights.SemiBold,Margin=new(0,10,0,6)});
        root.Children.Add(new TextBlock {Text="PDF의 좌표·지표 EL·층별 심도만으로 시추공 주변에 별도 일반모델 지층을 만듭니다. 각 위치에는 가장 가까운 시추공의 층서를 적용합니다. 생성 후 ‘지형·지층 중복체적’에서 지형 → 지층 순서로 선택해 물량을 기록하세요.",TextWrapping=TextWrapping.Wrap});
        var create=new Button {Content="시추주상도 기반 지층 생성",Name="CreateBoreholeGeology",Height=54,FontSize=18,FontWeight=FontWeights.SemiBold,Background=Brushes.RoyalBlue,Foreground=Brushes.White,Margin=new(0,8,0,0)};root.Children.Add(create);create.Click+=(_,_)=>Api(Generate);StickyActions(create);
    }
    internal void SetBoundaryForCheck(NtsGeometry boundary){Boundary=boundary;}
    public void Generate(UIApplication app)
    {
        var data=Input.Data();settings.BoreholeJson=Input.PathBox.Text;
        var result=SoilBuilder.GenerateStandalone(doc,data,settings,Boundary);app.ActiveUIDocument.RequestViewChange(result.View);
        SetStatus($"시추공 주변 별도 지층 생성 완료: {result.Elements.Count}개 / PDF 지표·심도 기준. ‘생성된 지층 검증’으로 체적과 시추공별 상하단을 확인하세요.");
    }
    private void ShowHoles(UIApplication app,BoreholeSet data)
    {
        var frame=SiteGeology.BoreholeFrame(data,settings.Frame,settings.BoreholeFrame);
        using var t=new Transaction(doc,"시추공 위치 검토 표시");t.Start();var old=Storage.Generated(doc).Where(x=>x.Info.Kind=="BoreholeMarker").Select(x=>x.Element.Id).ToList();if(old.Count>0)doc.Delete(old);
        var material=new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(m=>m.Name=="CivilBIM 시추공 위치")?.Id??Material.Create(doc,"CivilBIM 시추공 위치");((Material)doc.GetElement(material)).Color=new(20,100,230);var ids=new List<ElementId>();var points=new List<Point3>();
        foreach(var b in data.Boreholes){var point=frame.ToLocal(new(b.Easting,b.Northing,b.GroundElevation));points.Add(point);var solid=ModelBuilder.Cylinder(ModelBuilder.Xyz(point),point.Z,point.Z+1,.6,material);var shape=ModelBuilder.Shape(doc,solid,new ElementInfo("BoreholeMarker",b.Id,"시추공 위치",b.GroundElevation+1,b.GroundElevation,.6,0,1,b.Id,Input.PathBox.Text,"BH-MARKER",new(b.Easting,b.Northing,b.GroundElevation),frame,settings.Scale));shape.Name="시추공 위치 · "+b.Id;ids.Add(shape.Id);}
        var view=Views.Create(doc,"시추공 좌표 위치 검토",points);t.Commit();app.ActiveUIDocument.RequestViewChange(view);app.ActiveUIDocument.Selection.SetElementIds(ids);SetStatus($"{ids.Count}공의 PDF 좌표 위치를 파란 원기둥으로 표시했습니다. 지형을 수동 이동했다면 이 위치와 맞는지 확인하세요. 시추공 표시는 공사 경계가 아닙니다.");
    }

}
[Transaction(TransactionMode.Manual)]
public sealed class SoilCommand:IExternalCommand
{
    public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)
    {
        var ui=data.Application.ActiveUIDocument;if(ui==null||ui.Document.IsFamilyDocument)return Result.Cancelled;ModelessHost.Open(data.Application,"작업대지",()=>new SoilWindow(ui.Document,Storage.Load(ui.Document)));return Result.Succeeded;
    }
}
