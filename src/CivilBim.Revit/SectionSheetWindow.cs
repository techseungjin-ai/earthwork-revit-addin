using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CivilBim.Core;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ComboBox=System.Windows.Controls.ComboBox;

namespace CivilBim.Revit;

// The section and its original member list travel with the RVT, not the dialog.
public sealed record SectionMembers(List<string> Members);
public static class SectionSheetData
{
    private static readonly Guid Id=new("acbb9931-f343-4eb4-a1a7-83753b229914");
    private static Schema Schema()
    {
        var s=Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(Id);if(s!=null)return s;
        var b=new SchemaBuilder(Id);b.SetSchemaName("CivilBimSectionMembers");b.AddSimpleField("Json",typeof(string));return b.Finish();
    }
    public static void Write(View view,SectionMembers data){var e=new Entity(Schema());e.Set("Json",JsonSerializer.Serialize(data));view.SetEntity(e);}
    public static SectionMembers? Read(View view){var s=Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(Id);if(s==null)return null;var e=view.GetEntity(s);return e.IsValid()?JsonSerializer.Deserialize<SectionMembers>(e.Get<string>("Json")):null;}
    public static Transform ToSheet(View view,Viewport viewport)
    {
        var transforms=view.GetModelToProjectionTransforms();
        if(transforms.Count!=1)throw new InvalidDataException("분할 자르기 영역은 지원하지 않습니다. 단면의 자르기 분할을 해제하세요.");
        return viewport.GetProjectionToSheetTransform().Multiply(transforms.Single().GetModelToProjectionTransform());
    }
}

public sealed class SectionSheetWindow:ModelessWindow
{
    private readonly Document doc;
    private List<Point3> path=[];
    private List<ElementId> targets=[];
    private List<ElementId> pathElements=[];
    private List<ElementId> lines=[];
    private ElementId lineView=ElementId.InvalidElementId;
    private int planScale=100;
    private readonly ComboBox end=new(){ItemsSource=new[]{"하단을 선까지 맞추기","상단을 선까지 맞추기"},SelectedIndex=0,Margin=new(0,8,0,8),MinHeight=30};
    private readonly ComboBox mappingFamily=new(){MinHeight=28},mappingTop=new(){MinHeight=28},mappingBottom=new(){MinHeight=28};
    private readonly Dictionary<ElementId,(BottomChoice Bottom,string Top)> mappings=[];
    private List<ElementId> mappingMembers=[];
    private const string NoTop="(사용 안 함)";
    private readonly System.Windows.Controls.Primitives.ToggleButton customizeMapping=new(){IsChecked=false,Width=36,Height=20};
    private bool refreshingMapping;
    private readonly TextBlock mappingInfo=new(){TextWrapping=TextWrapping.Wrap,Margin=new(0,4,0,4)};
    private readonly TextBlock info=new(){TextWrapping=TextWrapping.Wrap,Margin=new(0,10,0,10)};
    public SectionSheetWindow(Document document)
    {
        doc=document;Title="경로 단면 · 시트 배치 · 선까지 높이 맞춤";Width=670;Height=580;MaxHeight=SystemParameters.WorkArea.Height-35;
        var panel=new StackPanel {Margin=new(22)};Content=new ScrollViewer {Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        panel.Children.Add(new TextBlock {Text="경로를 펼쳐 보고, 그린 선까지 높이 맞추기",FontSize=21,TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(new TextBlock {Text="처음: 평면에서 1 → 2 → 3. 이미 만든 CB 시트/단면이 있으면 그 뷰를 열고 4 → 5만 진행하세요. 이 창은 기존 부재의 높이를 조절합니다. CIP/H-PILE 배치는 먼저 완료하세요.",TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,8)});
        Button Add(string caption,Action<UIApplication> action,bool pick=false){var b=new Button{Content=caption,Padding=new(9),Margin=new(0,5,0,5)};panel.Children.Add(b);b.Click+=(_,_)=>Api(action,pick);return b;}
        Add("1. CAD 폴리선으로 평면 경로 선택",app=>{PlanView(app);TaskDialog.Show("경로 선택","평면에서 전개할 CAD 폴리선을 선택하세요.");path=ConnectedPath.Cad(DrawingSelections.Path(app.ActiveUIDocument,"전개 경로 선택"));pathElements=[];Summary();},true);
        Add("1. Revit 경로선 여러 개 선택 → 완료",app=>{PlanView(app);TaskDialog.Show("Revit 경로선 선택","연결된 모델선/상세선을 여러 개 선택한 뒤 완료를 누르세요. 선택 순서와 관계없이 끝점을 연결합니다. 갈라진 선이나 다른 경로는 함께 선택하지 마세요.");var picked=app.ActiveUIDocument.Selection.PickObjects(ObjectType.Element,new LineFilter(),"연결할 경로선 여러 개 선택 후 완료");path=ConnectedPath.Join(picked.DistinctBy(r=>r.ElementId).Select(r=>(IReadOnlyList<Point3>)((CurveElement)doc.GetElement(r)).GeometryCurve.Tessellate().Select(ModelBuilder.Meters).ToList()));pathElements=picked.Select(r=>r.ElementId).Distinct().ToList();Summary();},true);
        Add("1-1. 전개 시작점 / 진행 방향 지정",ChooseDirection,true);
        Add("2. 높이를 조절할 CIP / H-PILE 선택",app=>{TaskDialog.Show("수직부재 선택","단면에 표시하고 높이를 조절할 CIP/H-PILE을 선택한 뒤 완료를 누르세요.");targets=app.ActiveUIDocument.Selection.PickObjects(ObjectType.Element,new PileFilter(),"CIP/H-PILE 선택").Select(r=>r.ElementId).Distinct().ToList();RefreshMapping();Summary();},true);
        panel.Children.Add(info);
        mappingFamily.SelectionChanged+=(_,_)=>RefreshMappingParameters();
        mappingTop.SelectionChanged+=(_,_)=>MappingChanged();mappingBottom.SelectionChanged+=(_,_)=>MappingChanged();
        Add("선택한 경로선 표시",app=>{if(pathElements.Count==0)throw new InvalidDataException("화면 표시할 Revit 경로선이 없습니다. CAD 경로는 구간 수로 확인하세요.");app.ActiveUIDocument.Selection.SetElementIds(pathElements);});
        Add("선택한 수직부재 표시",app=>{if(targets.Count==0)throw new InvalidDataException("선택된 수직부재가 없습니다.");app.ActiveUIDocument.Selection.SetElementIds(targets);});
        Add("3. 경로별 단면 생성 + 새 시트에 연속 배치",Create);
        panel.Children.Add(new TextBlock {Text="3번 후 열린 시트에서 단면 안을 더블클릭해 활성화하세요. 해당 구간의 전개도를 같은 축척·EL로 맞추고 상단 또는 하단을 따라 Revit 상세선을 그립니다. 그 단면을 활성화한 상태에서 4번을 누르세요. 시트 전체에 이어 그릴 때는 뷰 비활성화 후 시트 상세선을 사용합니다. CAD 선은 4번에서 직접 선택되지 않습니다.",TextWrapping=TextWrapping.Wrap,Margin=new(0,10,0,10)});
        panel.Children.Add(end);
        end.SelectionChanged+=(_,_)=>{if(lines.Count==0)return;lines.Clear();lineView=ElementId.InvalidElementId;Summary();SetStatus("맞출 끝을 바꿨습니다. 4번에서 해당 기준선을 다시 선택하세요.");};
        Add("4. 높이 기준선 선택",PickHeightLines,true);
        var mappingRow=new DockPanel{Margin=new(0,8,0,4)};
        var switchRow=new StackPanel{Orientation=System.Windows.Controls.Orientation.Horizontal};
        DockPanel.SetDock(switchRow,Dock.Right);
        switchRow.Children.Add(new TextBlock{Text="OFF",VerticalAlignment=System.Windows.VerticalAlignment.Center,Margin=new(0,0,6,0)});
        customizeMapping.Template=(ControlTemplate)System.Windows.Markup.XamlReader.Parse(MappingSwitchTemplate);
        System.Windows.Automation.AutomationProperties.SetName(customizeMapping,"사용자 패밀리 높이 매개변수 변경");
        switchRow.Children.Add(customizeMapping);
        switchRow.Children.Add(new TextBlock{Text="ON",VerticalAlignment=System.Windows.VerticalAlignment.Center,Margin=new(6,0,0,0)});
        mappingRow.Children.Add(switchRow);
        mappingRow.Children.Add(new TextBlock{Text="사용자 패밀리 높이 매개변수 변경",VerticalAlignment=System.Windows.VerticalAlignment.Center});
        panel.Children.Add(mappingRow);
        var mappingPanel=new StackPanel{Visibility=System.Windows.Visibility.Collapsed};
        mappingPanel.Children.Add(mappingFamily);
        mappingPanel.Children.Add(new TextBlock{Text="상단 EL 매개변수"});
        mappingPanel.Children.Add(mappingTop);
        mappingPanel.Children.Add(new TextBlock{Text="하단 EL 매개변수"});
        mappingPanel.Children.Add(mappingBottom);
        mappingPanel.Children.Add(new TextBlock{Text="하단 EL 대신 깊이(Depth)를 쓰는 패밀리는 ‘길이로 계산’을 선택하세요.",TextWrapping=TextWrapping.Wrap});
        mappingPanel.Children.Add(mappingInfo);
        panel.Children.Add(mappingPanel);
        customizeMapping.Checked+=(_,_)=>mappingPanel.Visibility=System.Windows.Visibility.Visible;
        customizeMapping.Unchecked+=(_,_)=>mappingPanel.Visibility=System.Windows.Visibility.Collapsed;
        var apply=Add("5. 선택한 선까지 CIP / H-PILE 높이 적용",Apply);apply.Background=System.Windows.Media.Brushes.RoyalBlue;apply.Foreground=System.Windows.Media.Brushes.White;StickyActions(apply);Summary();
    }
    private const string MappingSwitchTemplate="""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         TargetType="ToggleButton">
            <Grid Background="Transparent">
                <Border x:Name="Track"
                        Background="#F3F4F6" BorderBrush="#94A3B8" BorderThickness="1" CornerRadius="10" />
                <Ellipse x:Name="Thumb"
                         Width="12" Height="12" Margin="4,0" HorizontalAlignment="Left"
                         VerticalAlignment="Center" Fill="#64748B" />
            </Grid>
            <ControlTemplate.Triggers>
                <Trigger Property="IsChecked" Value="True">
                    <Setter TargetName="Track" Property="Background" Value="#0279AD" />
                    <Setter TargetName="Track" Property="BorderBrush" Value="#0279AD" />
                    <Setter TargetName="Thumb" Property="HorizontalAlignment" Value="Right" />
                    <Setter TargetName="Thumb" Property="Fill" Value="White" />
                </Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True">
                    <Setter TargetName="Track" Property="BorderBrush" Value="#009CE0" />
                    <Setter TargetName="Track" Property="BorderThickness" Value="2" />
                </Trigger>
                <Trigger Property="IsEnabled" Value="False">
                    <Setter Property="Opacity" Value="0.55" />
                </Trigger>
            </ControlTemplate.Triggers>
        </ControlTemplate>
        """;
    private void Summary()=>info.Text=$"선택 현황: 경로 {Math.Max(0,path.Count-1)}구간 · 총 {path.Zip(path.Skip(1),(a,b)=>a.DistanceXY(b)).Sum():F3}m / Revit 경로선 {pathElements.Count}개 / 수직부재 {targets.Count}개 / 기준선 {lines.Count}개. 만든 단면과 부재 연결은 RVT에 저장됩니다.";
    private void ChooseDirection(UIApplication app)
    {
        PlanView(app);
        if(path.Count<2)throw new InvalidDataException("먼저 평면 경로선을 선택하세요.");
        var selection=app.ActiveUIDocument.Selection;
        var closed=path[0].DistanceXY(path[^1])<=.001;
        var start=ModelBuilder.Meters(selection.PickPoint(closed?"전개를 시작할 경로 꼭짓점 근처를 클릭하세요.":"전개를 시작할 경로 끝점 근처를 클릭하세요."));
        Point3? direction=closed?ModelBuilder.Meters(selection.PickPoint("시작점에서 진행할 바로 다음 선의 중간을 클릭하세요.")):null;
        path=ConnectedPath.Orient(path,start,direction);
        Summary();
        SetStatus($"전개 순서: 시작 ({path[0].X:F3}, {path[0].Y:F3}) → 다음 ({path[1].X:F3}, {path[1].Y:F3})m.");
    }
    private sealed record MappingChoice(ElementId Id,string Label){public override string ToString()=>Label;}
    private sealed record BottomChoice(string Name,bool FromLength)
    {
        public override string ToString()=>FromLength?$"길이로 계산 · {Name}":Name;
    }
    private static IEnumerable<Parameter> HeightParameters(FamilyInstance pile)=>pile.Parameters.Cast<Parameter>()
        .Where(p=>p.Id.Value>0&&!p.IsReadOnly&&p.StorageType==StorageType.Double&&p.Definition.GetDataType()==SpecTypeId.Length);
    private void RefreshMapping()
    {
        SetMappingMembers(targets);
    }
    private void SetMappingMembers(IEnumerable<ElementId> ids)
    {
        mappingMembers=ids.Distinct().ToList();
        var previous=(mappingFamily.SelectedItem as MappingChoice)?.Id;
        var choices=mappingMembers.Select(doc.GetElement).OfType<FamilyInstance>().GroupBy(p=>p.Symbol.Id)
            .Select(g=>new MappingChoice(g.Key,$"{g.First().Symbol.FamilyName} : {g.First().Symbol.Name} ({g.Count()}개)")).ToList();
        mappingFamily.ItemsSource=choices;
        mappingFamily.SelectedItem=choices.FirstOrDefault(c=>c.Id==previous)??choices.FirstOrDefault();
    }
    private void PickHeightLines(UIApplication app)
    {
        var ui=app.ActiveUIDocument;var source=ui.ActiveView;var contexts=Contexts(source);
        TaskDialog.Show("높이 기준선 선택","활성 단면의 모델선/상세선 또는 시트 위 상세선을 선택하세요. 여러 조각도 선택할 수 있습니다.");
        var picked=ui.Selection.PickObjects(ObjectType.Element,new LineFilter(),"높이 기준선 선택");
        lines=picked.Select(r=>r.ElementId).ToList();lineView=source.Id;
        SetMappingMembers(contexts.SelectMany(c=>SectionSheetData.Read(c.View)!.Members)
            .Select(uid=>doc.GetElement(uid)).OfType<FamilyInstance>().Select(p=>p.Id));
        Summary();
    }
    private List<(View View,Transform Map)> Contexts(View source)
    {
        var contexts=new List<(View View,Transform Map)>();
        if(source is ViewSheet sheet)
        {
            foreach(var id in sheet.GetAllViewports()){var vp=(Viewport)doc.GetElement(id);var view=(View)doc.GetElement(vp.ViewId);if(SectionSheetData.Read(view)!=null)contexts.Add((view,SectionSheetData.ToSheet(view,vp)));}
        }
        else if(source is ViewSection&&SectionSheetData.Read(source)!=null)contexts.Add((source,Transform.Identity));
        if(contexts.Count==0)throw new InvalidDataException("이 기능으로 만든 단면 또는 그 단면이 배치된 시트에서 선을 선택하세요.");
        return contexts;
    }
    private void RefreshMappingParameters()
    {
        if(mappingFamily.SelectedItem is not MappingChoice choice)
        {
            refreshingMapping=true;
            mappingTop.ItemsSource=null;mappingBottom.ItemsSource=null;mappingInfo.Text="";
            refreshingMapping=false;
            return;
        }
        var members=mappingMembers.Select(doc.GetElement).OfType<FamilyInstance>().Where(p=>p.Symbol.Id==choice.Id).ToList();
        if(members.Count==0)return;var first=members[0];
        var native=first.Symbol.Family.FamilyPlacementType==FamilyPlacementType.TwoLevelsBased;
        var names=HeightParameters(first).GroupBy(p=>p.Definition.Name).Where(g=>g.Count()==1).Select(g=>g.Key).Order().ToList();
        var bottomChoices=names.Select(n=>new BottomChoice(n,false)).Concat(names.Select(n=>new BottomChoice(n,true))).ToList();
        var saved=members.Select(Storage.Info).OfType<ElementInfo>().Select(x=>(x.LengthParameter,x.TopParameter,x.BottomParameter)).Distinct().ToList();
        refreshingMapping=true;
        mappingTop.IsEnabled=mappingBottom.IsEnabled=!native;
        mappingTop.ItemsSource=native?new[]{"상단 레벨 오프셋"}:new[]{NoTop}.Concat(names).ToArray();
        mappingBottom.ItemsSource=native?new[]{"기준 레벨 오프셋"}:bottomChoices;
        if(native)mappingTop.SelectedIndex=mappingBottom.SelectedIndex=0;
        else
        {
            (BottomChoice? Bottom,string Top) selected=(null,"");
            if(mappings.TryGetValue(choice.Id,out var draft))selected=draft;
            else if(saved.Count==1)
            {
                var data=saved[0];var fromLength=string.IsNullOrEmpty(data.BottomParameter);
                selected=(new(fromLength?data.LengthParameter:data.BottomParameter,fromLength),data.TopParameter);
            }
            mappingTop.SelectedItem=names.Contains(selected.Top)?selected.Top:NoTop;
            mappingBottom.SelectedItem=bottomChoices.FirstOrDefault(b=>b==selected.Bottom);
        }
        refreshingMapping=false;
        mappingInfo.Text=native?"이 유형의 상·하단은 Revit 레벨 오프셋으로 조정합니다.":saved.Count>1?"같은 유형에 서로 다른 기존 설정이 있습니다. 변경할 때 사용할 매개변수를 선택하세요.":"선택을 변경하면 단면 생성 또는 높이 적용 시 저장됩니다.";
    }
    private void MappingChanged()
    {
        if(customizeMapping.IsChecked!=true||refreshingMapping||mappingFamily.SelectedItem is not MappingChoice choice)return;
        mappings.Remove(choice.Id);
        if(mappingBottom.SelectedItem is not BottomChoice bottom)return;
        var top=mappingTop.SelectedItem as string??NoTop;if(top==NoTop)top="";
        mappings[choice.Id]=(bottom,top);
        if((top.Length==0&&!bottom.FromLength)||top==bottom.Name)
        {
            mappingInfo.Text="하단 EL을 직접 지정하려면 서로 다른 상단·하단 매개변수를 선택하세요.";
            return;
        }
        mappingInfo.Text=$"적용 예정 · 상단: {(top.Length==0?"사용 안 함":top)} / 하단: {bottom}";
    }
    private bool HasMapping(FamilyInstance pile)=>customizeMapping.IsChecked==true&&mappings.ContainsKey(pile.Symbol.Id);
    private ElementInfo WithMapping(FamilyInstance pile,ElementInfo info)
    {
        if(customizeMapping.IsChecked!=true||!mappings.TryGetValue(pile.Symbol.Id,out var m))return info;
        if(m.Top==m.Bottom.Name||(m.Top.Length==0&&!m.Bottom.FromLength))
            throw new InvalidDataException($"{pile.Symbol.FamilyName}: 상단·하단 EL에는 서로 다른 매개변수를 선택하세요. 깊이 매개변수는 ‘길이로 계산’으로 선택하세요.");
        return info with{LengthParameter=m.Bottom.FromLength?m.Bottom.Name:info.LengthParameter,TopParameter=m.Top,BottomParameter=m.Bottom.FromLength?"":m.Bottom.Name};
    }
    private void PlanView(UIApplication app){var active=app.ActiveUIDocument.ActiveView;if(active is not ViewPlan)throw new InvalidDataException("평면 뷰로 전환한 뒤 경로를 선택하세요.");planScale=Math.Max(1,active.Scale);}
    internal void ConfigureCheck(List<Point3> route,IEnumerable<ElementId> ids,int scale=100){path=route;targets=ids.ToList();planScale=scale;Summary();}
    internal void CreateCheck(UIApplication app)=>Create(app);
    internal void ApplyCheck(UIApplication app,ElementId source,IEnumerable<ElementId> curveIds,bool top){end.SelectedIndex=top?1:0;lineView=source;lines=curveIds.ToList();Apply(app);}
    private sealed class PileFilter:ISelectionFilter {public bool AllowElement(Element e)=>e is FamilyInstance;public bool AllowReference(Reference r,XYZ p)=>false;}
    private sealed class LineFilter:ISelectionFilter {public bool AllowElement(Element e)=>e is CurveElement;public bool AllowReference(Reference r,XYZ p)=>false;}
    private void Create(UIApplication app)
    {
        if(path.Count<2||targets.Count==0)throw new InvalidDataException("경로와 수직부재를 먼저 선택하세요.");
        var segments=path.Zip(path.Skip(1),(a,b)=>(A:ModelBuilder.Xyz(a),B:ModelBuilder.Xyz(b))).ToList();
        if(segments.Any(s=>new XYZ(s.B.X-s.A.X,s.B.Y-s.A.Y,0).GetLength()<doc.Application.ShortCurveTolerance))throw new InvalidDataException("경로에 너무 짧은 구간이 있습니다.");
        var piles=targets.Select(id=>doc.GetElement(id) as FamilyInstance??throw new InvalidDataException("선택 부재가 삭제되었습니다.")).ToList();
        var siblings=Storage.Generated(doc).Where(p=>p.Info.Kind is "CIP" or "HPile").GroupBy(p=>(p.Info.Source,p.Info.Batch,p.Info.ParentId)).ToDictionary(g=>g.Key,g=>g.Count());
        foreach(var pile in piles){var data=Storage.Info(pile);if(data!=null&&siblings.GetValueOrDefault((data.Source,data.Batch,data.ParentId))>1)throw new InvalidDataException("지층으로 분할하기 전 원말뚝으로 단면을 생성하세요.");}
        using var group=new TransactionGroup(doc,"독립 수직부재 경로 단면 작성");group.Start();var released=0;
        using(var register=new Transaction(doc,"기존 수직 패밀리 연결"))
        {
            register.Start();var settings=Storage.Load(doc);
            foreach(var pile in piles.Where(p=>Storage.Info(p)==null))
            {
                var b=FamilyBuilder.Bounds(pile);var length=(b.Max.Z-b.Min.Z)*.3048;
                var candidates=HeightParameters(pile).Where(p=>Math.Abs(p.AsDouble()*.3048-length)<.002).ToList();
                if(pile.Symbol.Family.FamilyPlacementType!=FamilyPlacementType.TwoLevelsBased&&candidates.Count!=1&&!HasMapping(pile))throw new InvalidDataException($"부재 {pile.Id}: 길이 매개변수를 자동으로 확인할 수 없습니다. 4번 아래 ‘사용자 패밀리 높이 매개변수 변경’을 켜고 지정하세요.");
                var info=new ElementInfo("HPile",pile.UniqueId,"",b.Max.Z*.3048+settings.Frame.Elevation,b.Min.Z*.3048+settings.Frame.Elevation,0,0,length,"","기존 패밀리",pile.UniqueId,ModelBuilder.Meters((b.Min+b.Max)/2),settings.Frame,settings.Scale){LengthParameter=candidates.FirstOrDefault()?.Definition.Name??""};info=WithMapping(pile,info);
                Storage.Write(pile,info);
            }
            foreach(var pile in piles.Where(p=>Storage.Info(p)!=null&&HasMapping(p)))Storage.Write(pile,WithMapping(pile,Storage.Info(pile)!));
            if(register.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("기존 부재 연결을 취소했습니다.");
        }
        using(var prepare=new Transaction(doc,"CIP H-PILE 자동결합 해제")){prepare.Start();released=FamilyBuilder.ReleaseVerticalJoins(doc,piles);if(prepare.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("자동결합 해제를 취소했습니다.");}
        var bounds=piles.ToDictionary(p=>p.Id,FamilyBuilder.Bounds);var assignment=segments.Select(_=>new List<FamilyInstance>()).ToList();
        double farthest=0;
        foreach(var pile in piles)
        {
            var nearest=ConnectedPath.Nearest(ModelBuilder.Meters(Center(bounds[pile.Id])),path);farthest=Math.Max(farthest,nearest.Distance);assignment[nearest.Segment].Add(pile);
        }
        var boxes=bounds.Values.ToList();var low=boxes.Min(b=>b.Min.Z)-ModelBuilder.Feet(3);var high=boxes.Max(b=>b.Max.Z)+ModelBuilder.Feet(3);
        var type=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(v=>v.ViewFamily==ViewFamily.Section)??throw new InvalidDataException("단면 뷰 유형이 없습니다.");
        // A DWG linked directly to the sheet in millimetres is full-size sheet
        // geometry. Use 1:1 for this working sheet so both share the same scale.
        const int scale=1;
        using var transaction=new Transaction(doc,"경로 단면 생성 및 시트 연속 배치");transaction.Start();
        var phases=doc.Phases.Cast<Phase>().ToList();var selectedPhases=piles.Select(p=>p.CreatedPhaseId).ToHashSet();var phase=phases.LastOrDefault(p=>selectedPhases.Contains(p.Id));
        var phaseFilter=new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>().FirstOrDefault(f=>f.GetPhaseStatusPresentation(ElementOnPhaseStatus.New)!=PhaseStatusPresentation.DontShow&&f.GetPhaseStatusPresentation(ElementOnPhaseStatus.Existing)!=PhaseStatusPresentation.DontShow);
        var generated=Storage.Generated(doc).Select(g=>g.Element).ToList();var imports=new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).ToElements();
        var foundations=new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFoundation).WhereElementIsNotElementType().ToElements();
        var sheet=ViewSheet.Create(doc,ElementId.InvalidElementId);sheet.Name="CivilBIM 경로 전개도";sheet.SheetNumber="CB-"+sheet.Id.Value;
        var cursor=ModelBuilder.Feet(.05);var originY=ModelBuilder.Feet(.05);var serial=0;
        foreach(var s in segments)
        {
            var x=new XYZ(s.B.X-s.A.X,s.B.Y-s.A.Y,0).Normalize();var normal=x.CrossProduct(XYZ.BasisZ);var length=new XYZ(s.B.X-s.A.X,s.B.Y-s.A.Y,0).GetLength();
            var memberPoints=assignment[serial].SelectMany(p=>Corners(bounds[p.Id])).ToList();
            // Each crop is exactly one developed station interval. Member extents
            // may expand depth, but must never expand the developed X interval.
            var minAlong=0.0;var maxAlong=length;
            var depth=Math.Max(ModelBuilder.Feet(1),memberPoints.Count==0?0:memberPoints.Max(p=>Math.Abs((p-s.A).DotProduct(normal)))+ModelBuilder.Feet(.1));
            // CreateSection's viewing direction is BasisZ; Revit computes RightDirection.
            var transform=Transform.Identity;transform.Origin=new(s.A.X,s.A.Y,0);transform.BasisX=x;transform.BasisY=XYZ.BasisZ;transform.BasisZ=normal;
            var box=new BoundingBoxXYZ {Transform=transform,Min=new(minAlong,low,-depth),Max=new(maxAlong,high,depth)};
            var view=ViewSection.CreateSection(doc,type.Id,box);
            if(view.RightDirection.DotProduct(x)<0)
            {
                doc.Delete(view.Id);transform.Origin=new(s.B.X,s.B.Y,0);transform.BasisX=-x;transform.BasisZ=-normal;box.Transform=transform;box.Min=new(length-maxAlong,low,-depth);box.Max=new(length-minAlong,high,depth);view=ViewSection.CreateSection(doc,type.Id,box);
            }
            if(view.RightDirection.DotProduct(x)<.999)throw new InvalidDataException("단면의 좌우 방향을 경로와 일치시키지 못했습니다.");
            view.Name=$"CB 전개 {sheet.Id.Value}-{++serial:00}";view.Scale=scale;view.DetailLevel=ViewDetailLevel.Fine;view.CropBoxActive=true;view.CropBoxVisible=false;
            view.ViewTemplateId=ElementId.InvalidElementId;
            if(phase!=null)view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.Set(phase.Id);
            if(phaseFilter!=null)view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.Set(phaseFilter.Id);
            foreach(Category category in doc.Settings.Categories)
                if((category.CategoryType==CategoryType.Model||category.CategoryType==CategoryType.Annotation)&&view.CanCategoryBeHidden(category.Id))
                    view.SetCategoryHidden(category.Id,category.Id.Value!=(long)BuiltInCategory.OST_StructuralFoundation&&category.Id.Value!=(long)BuiltInCategory.OST_Lines);
            var assigned=assignment[serial-1].Select(p=>p.Id).ToHashSet();var hidden=generated.Concat(imports).Concat(foundations).Where(e=>!assigned.Contains(e.Id)&&e.CanBeHidden(view)).Select(e=>e.Id).Distinct().ToList();if(hidden.Count>0)view.HideElements(hidden);
            view.SketchPlane=SketchPlane.Create(doc,Plane.CreateByNormalAndOrigin(view.ViewDirection,view.Origin));
            SectionSheetData.Write(view,new(assignment[serial-1].Select(p=>p.UniqueId).ToList()));
            doc.Regenerate();
            var viewport=Viewport.Create(doc,sheet.Id,view.Id,XYZ.Zero);doc.Regenerate();
            var map=SectionSheetData.ToSheet(view,viewport);var a=map.OfPoint(new XYZ(s.A.X,s.A.Y,low));
            // Adjacent route endpoints share one developed station.
            var move=new XYZ(cursor-a.X,originY-a.Y,0);viewport.SetBoxCenter(viewport.GetBoxCenter()+move);cursor+=length/scale;
        }
        if(transaction.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("단면/시트 생성을 취소했습니다.");
        group.Assimilate();app.ActiveUIDocument.RequestViewChange(sheet);SetStatus($"1:1 작업 시트: 경로 총 {path.Zip(path.Skip(1),(a,b)=>a.DistanceXY(b)).Sum():F3}m / 단면 {segments.Count}개 / 부재 {piles.Count}개. 단면 폭은 각 경로 길이와 같습니다. CAD를 mm로 링크하고 시작점·방향·EL을 맞추세요. 최대 이격 {farthest:F3}m.");
    }
    private static XYZ Center(BoundingBoxXYZ bounds)=>bounds.Transform.OfPoint((bounds.Min+bounds.Max)/2);
    private static IEnumerable<XYZ> Corners(BoundingBoxXYZ b)=>from x in new[]{b.Min.X,b.Max.X} from y in new[]{b.Min.Y,b.Max.Y} from z in new[]{b.Min.Z,b.Max.Z} select b.Transform.OfPoint(new XYZ(x,y,z));
    private void Apply(UIApplication app)
    {
        if(lines.Count==0||doc.GetElement(lineView) is not View source)throw new InvalidDataException("높이 기준선을 먼저 선택하세요.");
        var curves=lines.Select(id=>doc.GetElement(id) as CurveElement??throw new InvalidDataException("기준선이 삭제되었습니다. 다시 선택하세요.")).ToList();
        if(curves.Any(c=>c is DetailCurve&&c.OwnerViewId!=source.Id))throw new InvalidDataException("기준선과 활성 뷰가 다릅니다. 시트 상세선은 시트에서, 단면 선은 단면을 활성화한 뒤 선택하세요.");
        var contexts=Contexts(source);
        var plans=new List<(FamilyInstance Pile,ElementInfo Info,double Top,double Bottom)>();var settings=Storage.Load(doc);
        var skipped=new Dictionary<ElementId,string>();
        var applyTop=end.SelectedIndex==1;
        var projected=curves.SelectMany(c=>{var pts=c.GeometryCurve.Tessellate();return pts.Zip(pts.Skip(1),(a,b)=>(A:a,B:b));}).ToList();
        foreach(var (view,map) in contexts)
        {
            if(Math.Abs(view.UpDirection.Z)<.999)throw new InvalidDataException("수직 단면만 높이를 맞출 수 있습니다.");
            var right=map.OfVector(view.RightDirection).Normalize();
            var up=map.OfVector(XYZ.BasisZ);
            var crop=source is ViewSheet&&view.CropBoxActive?view.CropBox:null;
            var toCrop=crop?.Transform.Inverse;
            foreach(var uid in SectionSheetData.Read(view)!.Members)
            {
                var pile=doc.GetElement(uid) as FamilyInstance??throw new InvalidDataException("단면에 연결된 부재가 삭제되었습니다. 단면을 다시 생성하세요.");
                var live=FamilyBuilder.Bounds(pile);
                var p=Center(live);
                if(crop!=null&&toCrop!=null)
                {
                    // A section transform extends beyond its viewport. Do not
                    // match an off-crop member against the adjacent section.
                    var station=toCrop.OfPoint(p).X;
                    var tolerance=ModelBuilder.Feet(.002);
                    if(station<crop.Min.X-tolerance||station>crop.Max.X+tolerance)continue;
                }
                var q=map.OfPoint(new XYZ(p.X,p.Y,0));
                var profile=new List<PlanSegment>();
                foreach(var (a,b) in projected)
                {
                    if(source is not ViewSheet&&(Math.Abs((a-view.Origin).DotProduct(view.ViewDirection))>ModelBuilder.Feet(.02)||Math.Abs((b-view.Origin).DotProduct(view.ViewDirection))>ModelBuilder.Feet(.02)))continue;
                    Point3 Project(XYZ v)=>new((v-q).DotProduct(right),(v-q).DotProduct(up)/up.DotProduct(up)*.3048);
                    profile.Add(new(Project(a),Project(b)));
                }
                var height=SectionLineHeight.At(0,profile);if(height==null)continue;
                var el=height.Value+settings.Frame.Elevation;
                var top=applyTop?el:live.Max.Z*.3048+settings.Frame.Elevation;
                var bottom=applyTop?live.Min.Z*.3048+settings.Frame.Elevation:el;
                if(top<=bottom)
                {
                    skipped.TryAdd(pile.Id,$"ID {pile.Id} · 상단 EL {top:F3}m / 하단 EL {bottom:F3}m");
                    continue;
                }
                var stored=Storage.Info(pile)??throw new InvalidDataException($"부재 {pile.Id}: 부재 정보가 없습니다.");
                plans.Add((pile,WithMapping(pile,stored),top,bottom));
            }
        }
        var skippedDetail=skipped.Count==0?"":$"\n상단 EL이 하단 EL보다 높아야 합니다. 아래 {skipped.Count}개는 기존 높이를 유지했습니다.\n"+
            string.Join("\n",skipped.Values.Take(4))+(skipped.Count>4?$"\n외 {skipped.Count-4}개":"");
        if(plans.Count==0&&skipped.Count>0)throw new InvalidDataException("높이를 조정할 수 있는 부재가 없습니다."+skippedDetail);
        if(plans.Count==0)throw new InvalidDataException("기준선과 겹치는 연결 부재가 없습니다. 선을 부재 위치까지 연장하세요.");
        if(plans.GroupBy(p=>p.Pile.Id).Any(g=>g.Count()>1))throw new InvalidDataException("한 부재가 여러 전개 단면에 연결되어 있습니다. 적용할 단면을 활성화하고 선을 선택하세요.");
        using var group=new TransactionGroup(doc,"전개도 높이 적용 및 실제 형상 검산");group.Start();
        using var transaction=new Transaction(doc,"전개 단면 기준선까지 부재 높이 조정");transaction.Start();foreach(var p in plans)FamilyBuilder.Resize(doc,p.Pile,p.Info,p.Top,p.Bottom,settings);
        if(transaction.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("높이 적용을 취소했습니다.");
        FamilyBuilder.FinalizeVertical(doc,plans.Select(p=>p.Pile),settings);group.Assimilate();SetStatus($"{plans.Count}개 부재의 {(applyTop?"상단":"하단")}을 기준선까지 조정하고 실제 높이를 검산했습니다. 반대쪽 끝 높이는 유지했습니다."+skippedDetail,skipped.Count>0);
    }
}
