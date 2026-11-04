using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CivilBim.Core;
using ComboBox=System.Windows.Controls.ComboBox;
using TextBox=System.Windows.Controls.TextBox;

namespace CivilBim.Revit;

public sealed class HorizontalWindow:ModelessWindow
{
    public bool Applying {get;private set;}
    public string InputKey=>string.Join("|",Plan?.UniqueId,Elevation?.UniqueId,PlanLayer,TierLayer,Symbol,ThreeD,Datum1,Datum2);
    public ImportInstance? Plan {get;private set;}
    public ImportInstance? Elevation {get;private set;}
    public string PlanLayer {get;private set;}="";
    public string TierLayer {get;private set;}="";
    public string Symbol {get;private set;}="";
    public double Datum1 {get;private set;}
    public double Datum2 {get;private set;}
    public bool ThreeD {get;private set;}
    public HorizontalWindow(Document doc,Settings settings)
    {
        Title="STRUT / 띠장 · 전개도 단수 배치";Width=800;Height=800;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new StackPanel {Margin=new(22)};Content=new ScrollViewer {Content=root,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};void Label(string s)=>root.Children.Add(new TextBlock {Text=s,Margin=new(0,10,0,5)});
        root.Children.Add(new TextBlock {Text="평면 중심선 → 패밀리 → 전개도 높이·단수",FontSize=21,FontWeight=FontWeights.SemiBold,Margin=new(0,0,0,14)});
        var imports=new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Select(i=>new CadChoice(i,doc.GetElement(i.GetTypeId())?.Name??i.Name)).ToList();
        ComboBox Cad(string label){Label(label);var box=new ComboBox {ItemsSource=imports,MinHeight=32};ScreenChoices.Cad(this,root,box,doc);box.SelectedIndex=imports.Count>0?0:-1;return box;}
        var plan=Cad("1. 평면 CAD");plan.Name="HorizontalPlan";Label("평면 STRUT / 띠장 레이어");var planLayer=new ComboBox {Name="HorizontalPlanLayer",IsEditable=true,MinHeight=32};ScreenChoices.Layer(this,root,planLayer,plan,doc);
        Label("2. 배치 패밀리 유형 (선 기반 / 구조 프레임)");var families=ScreenChoices.Families(doc,ScreenChoices.Horizontal);var family=new ComboBox {Name="HorizontalFamily",ItemsSource=families,MinHeight=32};family.SelectedItem=families.FirstOrDefault(f=>f.Id==settings.StrutFamily);ScreenChoices.Family(this,root,family,doc,ScreenChoices.Horizontal);
        var elevation=Cad("3. 전개도 CAD");elevation.Name="HorizontalElevation";Label("전개도 단수 / 부재 레이어");var tiers=new ComboBox {Name="HorizontalTiers",IsEditable=true,MinHeight=32};ScreenChoices.Layer(this,root,tiers,elevation,doc);
        Label("전개도 높이축");var axis=new ComboBox {ItemsSource=new[]{"실제 Z 높이 — 수직으로 세운 전개도 링크","기존 XY 전개도 — EL 두 점 보정"},SelectedIndex=0,MinHeight=32};root.Children.Add(axis);
        var datumLabel=new TextBlock {Text="기존 XY 전개도 기준 EL 두 값 (m, 쉼표 구분)",Visibility=System.Windows.Visibility.Collapsed};root.Children.Add(datumLabel);var datum=new TextBox {Text="0,-10",Padding=new(7),Visibility=System.Windows.Visibility.Collapsed};root.Children.Add(datum);axis.SelectionChanged+=(_,_)=>{datum.Visibility=datumLabel.Visibility=axis.SelectedIndex==1?System.Windows.Visibility.Visible:System.Windows.Visibility.Collapsed;};
        root.Children.Add(new TextBlock {Text="실제 Z 모드는 전개도 링크를 수직으로 세워 높이를 맞추면 바로 읽습니다. 인식 결과를 검토한 뒤 하단의 적용 버튼을 누르세요. 같은 단의 상·하부 윤곽은 0.4m 이내에서 한 단으로 묶습니다.",TextWrapping=TextWrapping.Wrap,Margin=new(0,16,0,12)});
        void Update(ComboBox source,ComboBox target){if(source.SelectedItem is CadChoice c){target.ItemsSource=CadReader.Read(doc,c.Instance).Layers;target.Text=settings.StrutLayer;}}
        plan.SelectionChanged+=(_,_)=>Api(()=>Update(plan,planLayer));elevation.SelectionChanged+=(_,_)=>Api(()=>Update(elevation,tiers));Update(plan,planLayer);Update(elevation,tiers);
        var go=new Button {Content="1. 단수 인식 / 배치 결과 검토",Padding=new(12,10,12,10)};root.Children.Add(go);
        var apply=new Button {Content="2. STRUT · 띠장을 모델에 적용",Padding=new(12,10,12,10),Margin=new(0,6,0,0)};root.Children.Add(apply);
        void Submit(bool applying)=>Api(()=>
        {
            try
            {
                Plan=(plan.SelectedItem as CadChoice)?.Instance??throw new InvalidDataException("평면 CAD를 선택하세요.");Elevation=(elevation.SelectedItem as CadChoice)?.Instance??throw new InvalidDataException("전개도 CAD를 선택하세요.");Symbol=(family.SelectedItem as FamilyChoice)?.Id??throw new InvalidDataException("선 기반 패밀리를 선택하세요.");PlanLayer=planLayer.Text;TierLayer=tiers.Text;ThreeD=axis.SelectedIndex==0;
                if(!ThreeD){var values=datum.Text.Split(',');if(values.Length!=2)throw new InvalidDataException("기준 EL 두 값을 입력하세요.");Datum1=Guard.Number(values[0]);Datum2=Guard.Number(values[1]);Guard.Finite(Datum1,Datum2);}Applying=applying;Accept();
            }catch(Exception ex){SetStatus(ex.Message,true);}
        });
        go.Click+=(_,_)=>Submit(false);apply.Click+=(_,_)=>Submit(true);StickyActions(go,apply);
    }
}
public static class HorizontalService
{
    public static List<FamilyInstance> Place(Document doc,IEnumerable<PlanSegment> lines,IEnumerable<double> elevations,string symbol,Settings settings,string source)
    {
        var list=new List<FamilyInstance>();var batch=Guid.NewGuid().ToString();
        foreach(var line in lines)foreach(var el in elevations)
        {
            var a=ModelBuilder.Xyz(line.A with {Z=el-settings.Frame.Elevation});var b=ModelBuilder.Xyz(line.B with {Z=el-settings.Frame.Elevation});
            var instance=FamilyBuilder.Horizontal(doc,symbol,a,b,alignCenter:true);FamilyBuilder.Track(doc,instance,new ElementInfo("Strut",$"STRUT-{list.Count+1:D5}","STRUT / 띠장",el,el,0,0,line.Length,"",source,batch,settings.Frame.ToSurvey(line.A),settings.Frame,settings.Scale));list.Add(instance);
        }
        return list;
    }
}
[Transaction(TransactionMode.Manual)]
public sealed class HorizontalCommand:IExternalCommand
{
    public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)
    {
        var initial=data.Application.ActiveUIDocument;if(initial==null)return Result.Cancelled;var doc=initial.Document;var settings=Storage.Load(doc);
        ModelessHost.Open(data.Application,"수평부재시트",()=>new SheetHorizontalWindow(doc,settings));return Result.Succeeded;
    }
    internal static void Bind(Document doc,Settings settings,HorizontalWindow window)
    {
        List<PlanSegment>? cachedLines=null;List<double>? cachedLevels=null;string cachedKey="",cachedSource="",cachedGeometry="";
        string GeometryKey()=>System.Text.Json.JsonSerializer.Serialize(new[]{(window.Plan!,window.PlanLayer),(window.Elevation!,window.TierLayer)}.Select(pair=>CadReader.Read(doc,pair.Item1).Paths.Where(p=>p.Layer==pair.Item2).Select(p=>p.Points.Select(ModelBuilder.Meters).ToList()).ToList()).ToList());
        window.Submitted+=()=>window.Api(app=>
        {
            var ui=app.ActiveUIDocument;
            if(window.Applying)
            {
                if(cachedLines==null||cachedLevels==null||cachedKey!=window.InputKey)throw new InvalidDataException("먼저 ‘단수 인식 / 배치 결과 검토’를 실행하세요. 입력이 바뀌면 다시 인식해야 합니다.");
                if(cachedGeometry!=GeometryKey())throw new InvalidDataException("검토 후 CAD 위치/형상이 바뀌었습니다. 다시 단수를 인식한 뒤 적용하세요.");
                using var applyTransaction=new Transaction(doc,"검토한 STRUT / 띠장 다단 적용");applyTransaction.Start();var previous=Storage.Generated(doc).Where(x=>x.Info.Source==cachedSource&&x.Info.Kind=="Strut").Select(x=>x.Element.Id).ToList();if(previous.Count>0)doc.Delete(previous);var placed=HorizontalService.Place(doc,cachedLines,cachedLevels,window.Symbol,Storage.Load(doc),cachedSource);settings.StrutFamily=window.Symbol;settings.StrutLayer=window.PlanLayer;Storage.Write(doc.ProjectInformation,settings);if(applyTransaction.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("수평부재 적용이 취소되었습니다.");ui.Selection.SetElementIds(placed.Select(p=>p.Id).ToList());window.SetStatus($"{cachedLines.Count}개 × {cachedLevels.Count}단 = {placed.Count}개 모델 적용 완료.");return;
            }
            cachedLines=null;cachedLevels=null;
            if(Math.Abs(ui.ActiveView.ViewDirection.Z)<.999)throw new InvalidDataException("평면 뷰에서 수평부재 영역을 지정하세요.");
            var plan=CadReader.Read(doc,window.Plan!);if(ui.ActiveView is ViewPlan {CropBoxActive:true} view){var box=view.CropBox;plan.Crop(box.Transform.OfPoint(box.Min),box.Transform.OfPoint(box.Max));}
            else{var box=ui.Selection.PickBox(PickBoxStyle.Directional,"수평부재를 생성할 평면 영역을 지정하세요.");plan.Crop(box.Min,box.Max);}
            var lines=HorizontalRecognition.Centerlines(plan.Paths.Where(p=>p.Layer==window.PlanLayer).Select(p=>new PlanPath(p.Points.Select(ModelBuilder.Meters).ToList(),p.Closed)));
            if(lines.Count==0)throw new InvalidDataException("평면 레이어에서 중심선/세장한 외곽을 찾지 못했습니다.");
            var elevation=CadReader.Read(doc,window.Elevation!);List<double> levels;
            if(window.ThreeD)
            {
                var segments=elevation.Paths.Where(p=>p.Layer==window.TierLayer).SelectMany(p=>p.Points.Zip(p.Points.Skip(1),(a,b)=>new PlanSegment(ModelBuilder.Meters(a),ModelBuilder.Meters(b)))).Where(s=>s.A.DistanceXY(s.B)>.2&&Math.Abs(s.A.Z-s.B.Z)<.005).ToList();
                if(segments.Count==0)throw new InvalidDataException("전개도 실제 Z에서 수평 단선을 찾지 못했습니다.");
                var axis=segments.MaxBy(s=>s.Length)!;var dx=(axis.B.X-axis.A.X)/axis.Length;var dy=(axis.B.Y-axis.A.Y)/axis.Length;var normals=segments.SelectMany(s=>new[]{s.A,s.B}).Select(p=>p.X*dy-p.Y*dx).ToList();if(normals.Max()-normals.Min()>.02)throw new InvalidDataException("단선들이 같은 수직 전개면에 있지 않습니다. 전개도를 세워 Z를 맞추거나 기존 XY 보정 모드를 선택하세요.");
                levels=HorizontalRecognition.DrawingTiers(segments.Select(s=>new PlanSegment(new(0,s.A.Z),new(s.A.DistanceXY(s.B),s.B.Z))),0,settings.Frame.Elevation,1);
            }
            else
            {
                var first=ModelBuilder.Meters(ui.Selection.PickPoint($"전개도에서 EL {window.Datum1}m 기준점을 선택하세요."));var second=ModelBuilder.Meters(ui.Selection.PickPoint($"EL {window.Datum2}m 기준점을 선택하세요."));
                if(Math.Abs(first.Y-second.Y)<.001)throw new InvalidDataException("두 기준점의 Y 좌표가 같습니다.");
                var box=ui.Selection.PickBox(PickBoxStyle.Directional,"단수를 읽을 전개도 영역을 지정하세요.");elevation.Crop(box.Min,box.Max);
                var segments=elevation.Paths.Where(p=>p.Layer==window.TierLayer).SelectMany(p=>p.Points.Zip(p.Points.Skip(1),(a,b)=>new PlanSegment(ModelBuilder.Meters(a),ModelBuilder.Meters(b))));
                levels=HorizontalRecognition.DrawingTiers(segments,first.Y,window.Datum1,(window.Datum2-window.Datum1)/(second.Y-first.Y));
            }
            if(levels.Count==0)throw new InvalidDataException("전개도 단수를 인식하지 못했습니다.");
            var source="STRUT:"+window.Plan!.UniqueId+":"+window.PlanLayer+":"+plan.RegionKey;
            cachedLines=lines;cachedLevels=levels;cachedKey=window.InputKey;cachedSource=source;cachedGeometry=GeometryKey();window.SetStatus($"평면 {lines.Count}개 × {levels.Count}단 = {lines.Count*levels.Count}개 인식. EL: {string.Join(", ",levels.Select(z=>z.ToString("F3")))}m. 아직 생성하지 않았습니다. 아래 ‘STRUT · 띠장을 모델에 적용’을 누르세요.");
        },true);
    }
}
