using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CivilBim.Core;
using System.Windows;
using System.Windows.Controls;
using ComboBox=System.Windows.Controls.ComboBox;
using TextBox=System.Windows.Controls.TextBox;

namespace CivilBim.Revit;

public sealed class DrawingDevelopmentWindow:ModelessWindow
{
    private readonly Document doc;
    internal HashSet<ElementId> Targets {get;set;}=[];
    internal List<Point3> Alignment {get;set;}=[];
    internal List<PlanSegment> Upper {get;set;}=[];
    internal List<PlanSegment> Lower {get;set;}=[];
    private readonly CheckBox reversePlan=new(){Content="평면 경로 진행 방향 뒤집기",Margin=new(0,7,0,5)},reverseProfile=new(){Content="전개도 좌우 진행 방향 뒤집기",Margin=new(0,5,0,8)};
    private readonly TextBlock summary=new(){TextWrapping=TextWrapping.Wrap,Margin=new(0,12,0,12)};
    private readonly ComboBox mode=new(){ItemsSource=new[]{"Revit 상·하부선 직접 선택","CAD 수직 말뚝선 · 실제 Z 사용","CAD XY 전개도 · EL 기준점 하나 사용"},SelectedIndex=1,MinHeight=30};
    private readonly ComboBox profileCad=new(){MinHeight=30},profileLayer=new(){IsEditable=true,MinHeight=30};
    private readonly TextBox datumEl=new(){Text="",Padding=new(6)},startStation=new(){Text="0",Padding=new(6)};
    private Point3? datum;private Point3 flatX=new(1,0),flatY=new(0,1);
    public DrawingDevelopmentWindow(Document document,IEnumerable<ElementId>? selected=null)
    {
        doc=document;Targets=selected?.ToHashSet()??[];Title="전개도 실제 Z → CIP / H-PILE 깊이";Width=780;Height=750;MinHeight=570;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new StackPanel {Margin=new(22)};Content=new ScrollViewer {Content=root,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        root.Children.Add(new TextBlock {Text="평면 폴리선 → 전개도 상·하부 선 → 깊이 적용",FontSize=21,TextWrapping=TextWrapping.Wrap});
        root.Children.Add(new TextBlock {Text="전개도 링크를 수직으로 세우고 실제 Z 높이를 맞춘 뒤 사용하세요. 기준 EL 두 점은 필요하지 않습니다. 1:1 경로의 첫 점이 전개도 수평 시작점에 대응하며, 방향이 반대면 아래에서 뒤집습니다.",TextWrapping=TextWrapping.Wrap,Margin=new(0,12,0,12)});
        Button Button(string text,Action<UIApplication> action,bool pick=false){var b=new Button {Content=text,Padding=new(10),Margin=new(0,5,0,5)};root.Children.Add(b);b.Click+=(_,_)=>Api(action,pick);return b;}
        Button("1. Revit에서 선택한 CIP / H-PILE 가져오기",app=>{Targets=app.ActiveUIDocument.Selection.GetElementIds().ToHashSet();ShowSummary();});
        Button("2. 평면 경로 POLYLINE 선택",app=>{Alignment=DrawingSelections.Path(app.ActiveUIDocument,"평면의 연결된 전개 경로 폴리선을 선택하세요. 첫 꼭짓점이 시작점입니다.");ShowSummary();},true);
        root.Children.Add(reversePlan);
        root.Children.Add(new TextBlock {Text="전개도 인식 방식",Margin=new(0,8,0,4)});root.Children.Add(mode);
        root.Children.Add(new TextBlock {Text="전개도 CAD / 말뚝 수직선 레이어 (CIP·H-PILE은 각각 해당 레이어로 처리)",TextWrapping=TextWrapping.Wrap});ScreenChoices.Cad(this,root,profileCad,doc);ScreenChoices.Layer(this,root,profileLayer,profileCad,doc);
        profileCad.SelectionChanged+=(_,_)=>Api(()=>{if(profileCad.SelectedItem is CadChoice c)profileLayer.ItemsSource=CadReader.Read(doc,c.Instance).Layers;});
        var imports=ScreenChoices.Imports(doc);profileCad.ItemsSource=imports;profileCad.SelectedItem=imports.FirstOrDefault(c=>c.Label.Contains("전개"))??imports.FirstOrDefault();
        root.Children.Add(new TextBlock {Text="XY 도면일 때: 기준선의 EL(m)을 입력하고 그 선 위 점 하나 선택",TextWrapping=TextWrapping.Wrap});root.Children.Add(datumEl);
        Button("XY 전개도 기준 EL 점 하나 선택",app=>{if(Math.Abs(app.ActiveUIDocument.ActiveView.ViewDirection.Z)<.999)throw new InvalidDataException("XY 전개도 기준점은 평면 뷰에서 선택하세요.");datum=ModelBuilder.Meters(app.ActiveUIDocument.Selection.PickPoint("알려진 EL의 수평 기준선 위 점을 선택하세요."));flatX=ModelBuilder.Meters(app.ActiveUIDocument.ActiveView.RightDirection.Multiply(1/.3048));flatY=ModelBuilder.Meters(app.ActiveUIDocument.ActiveView.UpDirection.Multiply(1/.3048));SetStatus("기준점 선택 완료. EL 값을 입력하고 검토하세요.");},true);
        Button("회전된 XY 도면만: Revit 가로 기준선 선택 (왼쪽→오른쪽)",app=>{var p=DrawingSelections.RevitPath(app.ActiveUIDocument,"전개도의 가로 방향을 따라 왼쪽에서 오른쪽으로 그린 Revit 선을 선택하세요.",out _);var d=p[0].DistanceXY(p[^1]);if(d<.01)throw new InvalidDataException("수평 방향 길이가 없습니다.");flatX=new((p[^1].X-p[0].X)/d,(p[^1].Y-p[0].Y)/d);flatY=new(-flatX.Y,flatX.X);SetStatus("전개도 가로/세로 방향 설정 완료. EL 점→방향선 순서로 선택하세요. EL 점을 다시 선택하면 뷰 방향으로 초기화됩니다.");},true);
        root.Children.Add(new TextBlock {Text="전개도 구간 시작의 평면 누적거리 (m) · 전체 경로는0"});root.Children.Add(startStation);
        Button("3. 전개도 Revit 상부 선 선택",app=>{Upper=DrawingSelections.RevitLines(app.ActiveUIDocument,"전개도 상부 Revit 참조선/모델선을 선택하고 완료하세요.");ShowSummary();},true);
        Button("4. 전개도 Revit 하부 선 선택",app=>{Lower=DrawingSelections.RevitLines(app.ActiveUIDocument,"전개도 하부 Revit 참조선/모델선을 선택하고 완료하세요.");ShowSummary();},true);
        root.Children.Add(reverseProfile);root.Children.Add(summary);
        root.Children.Add(new TextBlock {Text="상하부선은 Revit 참조선·참조평면·모델/상세선을 선택합니다. 실제 Z를 가진 같은 수직면의 같은 전개 구간이어야 합니다. 선택한 선을 수정했다면 다시 선택하세요.",TextWrapping=TextWrapping.Wrap,Margin=new(0,6,0,8)});
        var preview=Button("깊이 인식 / 결과 검토",_=>Preview());var apply=Button("선택한 CIP / H-PILE 깊이를 모델에 적용",_=>Apply());StickyActions(preview,apply);ShowSummary();
    }
    private void ShowSummary()=>summary.Text=$"대상 선택 {Targets.Count}개 / 평면 경로 {Alignment.Count}점 / 상부 {Upper.Count}선 / 하부 {Lower.Count}선";
    internal void SetProfileMode(int index)=>mode.SelectedIndex=index;
    internal List<(FamilyInstance Instance,ElementInfo Info,double Top,double Bottom)> Plan()
    {
        var piles=Targets.Select(doc.GetElement).OfType<FamilyInstance>().Select(i=>(Instance:i,Info:Storage.Info(i))).Where(p=>p.Info?.Kind is "CIP" or "HPile").ToList();
        if(piles.Count==0)throw new InvalidDataException("CIP/H-PILE을 선택하고 대상 가져오기를 누르세요.");if(Alignment.Count<2)throw new InvalidDataException("평면 폴리선을 먼저 선택하세요.");
        var siblings=Storage.Generated(doc).Where(p=>p.Info.Kind is "CIP" or "HPile").GroupBy(p=>(p.Info.Source,p.Info.Batch,p.Info.ParentId)).ToDictionary(g=>g.Key,g=>g.Count());
        if(piles.Any(p=>siblings[(p.Info!.Source,p.Info.Batch,p.Info.ParentId)]>1))throw new InvalidDataException("지층 분할 전 원말뚝을 선택하세요.");
        var path=reversePlan.IsChecked==true?Alignment.AsEnumerable().Reverse().ToList():Alignment;
        var settings=Storage.Load(doc);Func<double,(double Top,double Bottom)> read;
        if(mode.SelectedIndex==0)
        {var profile=new VerticalProfile(Upper,Lower,reverseProfile.IsChecked==true);profile.CheckLength(path.Zip(path.Skip(1),(a,b)=>a.DistanceXY(b)).Sum());read=s=>profile.At(s,settings.Frame.Elevation);}
        else
        {
            var cad=(profileCad.SelectedItem as CadChoice)?.Instance??throw new InvalidDataException("전개도 CAD를 선택하세요.");var reader=CadReader.Read(doc,cad);var segments=reader.Paths.Where(p=>p.Layer==profileLayer.Text).SelectMany(p=>p.Points.Zip(p.Points.Skip(1),(a,b)=>new PlanSegment(ModelBuilder.Meters(a),ModelBuilder.Meters(b)))).ToList();
            var isFlat=mode.SelectedIndex==2;if(isFlat&&datum==null)throw new InvalidDataException("XY 도면의 알려진 EL 기준점 하나를 선택하세요.");
            var horizontal=isFlat?flatX:HorizontalAxis(segments);var vertical=isFlat?flatY:new Point3(0,0,1);
            var profile=new MemberDepthProfile(segments,horizontal,vertical,isFlat?datum!.Value:new(),isFlat?Guard.Number(datumEl.Text):settings.Frame.Elevation,reverseProfile.IsChecked==true);var offset=Guard.Number(startStation.Text);read=s=>profile.At(s-offset);
            summary.Text=$"전개도 말뚝선 {profile.Members.Count}개 / 수평 폭{profile.Length:F3}m. 부재 사이 높이는 선형 대응. 구간 시작거리{offset:F3}m. 평면 경로와 전개도 시작·방향을 검토하세요.";
        }
        return piles.Select(p=>{ModelBuilder.VerifyUnchanged(p.Instance,p.Info!);var station=DevelopmentMapping.Station(settings.Frame.ToLocal(p.Info!.Position),path);var depth=read(station);return(p.Instance,p.Info!,depth.Top,depth.Bottom);}).ToList();
    }
    private static Point3 HorizontalAxis(List<PlanSegment> lines)
    {
        var points=lines.SelectMany(s=>new[]{s.A,s.B}).ToList();if(points.Count<2)throw new InvalidDataException("레이어에 선이 없습니다.");var xmin=points.MinBy(p=>p.X);var xmax=points.MaxBy(p=>p.X);var ymin=points.MinBy(p=>p.Y);var ymax=points.MaxBy(p=>p.Y);var (a,b)=xmin.DistanceXY(xmax)>=ymin.DistanceXY(ymax)?(xmin,xmax):(ymin,ymax);var d=a.DistanceXY(b);if(d<.01)throw new InvalidDataException("전개도 수평 축을 찾지 못했습니다.");return new((b.X-a.X)/d,(b.Y-a.Y)/d);
    }
    public void Preview()
    {
        var plan=Plan();SetStatus($"{plan.Count}개 인식 · 길이 {plan.Min(p=>p.Top-p.Bottom):F3}~{plan.Max(p=>p.Top-p.Bottom):F3}m. 아직 모델은 변경하지 않았습니다.\n"+string.Join(" / ",plan.Take(8).Select(p=>$"{p.Info.ParentId}: EL {p.Top:F2}~{p.Bottom:F2}")));
    }
    public int Apply()
    {
        var plan=Plan();var settings=Storage.Load(doc);using var t=new Transaction(doc,"전개도 실제 Z로 수직부재 깊이 적용");t.Start();foreach(var p in plan)FamilyBuilder.Resize(doc,p.Instance,p.Info,p.Top,p.Bottom,settings);if(t.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("깊이 변경이 취소되었습니다.");SetStatus($"{plan.Count}개 수직부재 깊이 적용 완료. 실제 상단·하단 EL을 검사했습니다.");return plan.Count;
    }
}
[Transaction(TransactionMode.Manual)]
public sealed class DrawingDevelopmentCommand:IExternalCommand
{
    public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)
    {var ui=data.Application.ActiveUIDocument;if(ui==null)return Result.Cancelled;ModelessHost.Open(data.Application,"경로단면시트",()=>new SectionSheetWindow(ui.Document));return Result.Succeeded;}
}

