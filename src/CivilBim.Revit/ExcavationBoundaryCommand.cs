using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CivilBim.Core;
using NetTopologySuite.Geometries;
using System.Windows;
using System.Windows.Controls;
using ComboBox=System.Windows.Controls.ComboBox;
using TextBox=System.Windows.Controls.TextBox;

namespace CivilBim.Revit;

[Transaction(TransactionMode.Manual)]
public sealed class ExcavationBoundaryCommand:IExternalCommand
{
    public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)
    {
        var ui=data.Application.ActiveUIDocument;if(ui==null)return Result.Cancelled;
        ModelessHost.Open(data.Application,"굴착 경계",()=>new ExcavationBoundaryWindow(ui.Document));return Result.Succeeded;
    }
}
public sealed class ExcavationBoundaryWindow:ModelessWindow
{
    private readonly Document doc;
    private readonly ComboBox cad=new(){MinHeight=32},layer=new(){IsEditable=true,MinHeight=32},kind=new(){MinHeight=32,ItemsSource=new[]{"바닥","지형솔리드"},SelectedIndex=0},type=new(){MinHeight=32},level=new(){MinHeight=32};
    private readonly TextBox el=new(){Text="0",Padding=new(6)};
    private readonly TextBox gap=new(){Text="2",Padding=new(6)};
    private readonly TextBox shortLine=new(){Text="0",Padding=new(6)};
    private Polygon? clip;
    private readonly TextBlock details=new(){TextWrapping=TextWrapping.Wrap,Margin=new(0,10,0,8)};
    private sealed record TypeChoice(ElementId Id,string Label){public override string ToString()=>Label;}
    public ExcavationBoundaryWindow(Document document)
    {
        doc=document;Title="CAD 경계 → 굴착용 바닥 / 지형솔리드";Width=650;Height=650;MaxHeight=SystemParameters.WorkArea.Height-35;
        var p=new StackPanel{Margin=new(22)};Content=new ScrollViewer{Content=p,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        p.Children.Add(new TextBlock{Text="CAD 경계로 굴착면 만들기",FontSize=21,FontWeight=FontWeights.SemiBold});
        void Label(string text)=>p.Children.Add(new TextBlock{Text=text,Margin=new(0,12,0,4)});
        Button Add(string text,Action<UIApplication> action,bool pick=false){var b=new Button{Content=text,Padding=new(8),Margin=new(0,5,0,5)};p.Children.Add(b);b.Click+=(_,_)=>Api(action,pick);return b;}
        Label("1. 평면 CAD");cad.ItemsSource=ScreenChoices.Imports(doc);ScreenChoices.Cad(this,p,cad,doc);
        Label("2. 사면 구분선 레이어");ScreenChoices.Layer(this,p,layer,cad,doc);
        cad.SelectionChanged+=(_,_)=>Api(()=>{if(cad.SelectedItem is CadChoice c)layer.ItemsSource=CadReader.Read(doc,c.Instance).Layers;});
        Label("3. 작성할 요소");p.Children.Add(kind);Label("요소 유형");p.Children.Add(type);
        void Types(){var old=(type.SelectedItem as TypeChoice)?.Id;var elements=kind.SelectedIndex==0?new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<Element>():new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).Cast<Element>();var choices=elements.Select(e=>new TypeChoice(e.Id,e.Name)).OrderBy(e=>e.Label).ToList();type.ItemsSource=choices;type.SelectedItem=choices.FirstOrDefault(c=>c.Id==old)??choices.FirstOrDefault();}
        kind.SelectionChanged+=(_,_)=>Api(Types);Types();
        Label("4. 기준 레벨과 굴착면 상단 EL (m)");var levels=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l=>l.Elevation).Select(l=>new TypeChoice(l.Id,l.Name)).ToList();level.ItemsSource=levels;level.SelectedItem=levels.FirstOrDefault();p.Children.Add(level);p.Children.Add(el);
        Label("선 연결 허용 오차 (0~500mm)");p.Children.Add(gap);
        Label("이 길이 이하의 CAD 선 제외 (mm, 0 = 사용 안 함)");p.Children.Add(shortLine);
        p.Children.Add(new TextBlock{Text="CIP 중심 또는 작업 외곽을 따라 그린 Revit 선을 선택하세요. CAD 사면 구분선으로 영역을 나누고 클릭한 한 구역을 만듭니다. 하위요소 높이는 생성 후 수정할 수 있습니다.",TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,4)});
        Add("1. Revit 외곽선 여러 개 선택 → 완료",app=>{var refs=app.ActiveUIDocument.Selection.PickObjects(ObjectType.Element,new BoundaryLineFilter(),"CIP 중심/작업 외곽을 잇는 Revit 선 선택 후 완료");var pieces=refs.DistinctBy(r=>r.ElementId).Select(r=>(IReadOnlyList<Point3>)((CurveElement)doc.GetElement(r)).GeometryCurve.Tessellate().Select(ModelBuilder.Meters).ToList());var points=ConnectedPath.Join(pieces);clip=Polygon(points,true);details.Text=$"Revit 외곽선 {refs.Count}개 선택됨 · 사면 구분선으로 나눌 영역 {clip.Area:F2}㎡";app.ActiveUIDocument.Selection.SetElementIds(refs.Select(r=>r.ElementId).ToList());},true);
        Add("또는 CAD 폐합 외곽선 선택",app=>{var points=DrawingSelections.Path(app.ActiveUIDocument,"닫힌 CAD 외곽 폴리선을 선택하세요.");clip=Polygon(points,true);details.Text="CAD 외곽선 선택됨: 사면 구분선으로 영역을 나눕니다.";},true);
        Add("중심 경계 해제",_=>{clip=null;details.Text="중심 경계 없이 원래 폐합선으로 생성합니다.";});p.Children.Add(details);
        var create=Add("2. 구역 여러 곳 클릭 → Esc → 일괄 생성",Create,true);create.Background=System.Windows.Media.Brushes.RoyalBlue;create.Foreground=System.Windows.Media.Brushes.White;StickyActions(create);
    }
    private sealed class BoundaryLineFilter:ISelectionFilter{public bool AllowElement(Element e)=>e is CurveElement;public bool AllowReference(Reference r,XYZ p)=>false;}
    private static Polygon Polygon(IReadOnlyList<Point3> points,bool requireClosed)
    {
        if(points.Count<4||(requireClosed&&points[0].DistanceXY(points[^1])>.001))throw new InvalidDataException("닫힌 폴리선을 선택하세요. CIP 중심을 따라 선을 연결한 뒤 폐합해야 합니다.");
        var coordinates=points.Select(q=>new Coordinate(q.X,q.Y)).ToList();if(!coordinates[0].Equals2D(coordinates[^1]))coordinates.Add(coordinates[0].Copy());
        var poly=Polygons.Factory.CreatePolygon(coordinates.ToArray());if(!poly.IsValid||poly.Area<.01)throw new InvalidDataException("경계가 교차하거나 너무 작습니다. CAD 선을 확인하세요.");return poly;
    }
    private void Create(UIApplication app)
    {
        if(app.ActiveUIDocument.ActiveView is not ViewPlan)throw new InvalidDataException("평면 뷰에서 실행하세요.");
        var selected=cad.SelectedItem as CadChoice??throw new InvalidDataException("CAD를 선택하세요.");if(string.IsNullOrWhiteSpace(layer.Text))throw new InvalidDataException("경계 레이어를 선택하세요.");
        var selectedType=type.SelectedItem as TypeChoice??throw new InvalidDataException("요소 유형을 선택하세요.");var selectedLevel=level.SelectedItem as TypeChoice??throw new InvalidDataException("기준 레벨을 선택하세요.");var elevation=Guard.Number(el.Text);Guard.Finite(elevation);
        if(clip==null)throw new InvalidDataException("먼저 CIP 중심 또는 작업 외곽의 폐합 경계를 선택하세요.");
        var reader=CadReader.Read(doc,selected.Instance);var paths=reader.Paths.Where(x=>x.Layer==layer.Text).Select(x=>(IReadOnlyList<Point3>)x.Points.Select(ModelBuilder.Meters).ToList()).ToList();if(paths.Count==0)throw new InvalidDataException("선택한 레이어에 선이 없습니다.");
        var tolerance=Guard.Number(gap.Text);
        var regions=ExcavationRegions.Split(paths,clip,tolerance/1000,Guard.Number(shortLine.Text)/1000);TaskDialog.Show("만들 구역 선택",$"외곽 안에서 {regions.Count}개 구역을 찾았습니다. 원하는 구역을 여러 번 클릭하고 Esc를 누르세요. 같은 곳을 다시 클릭하면 선택이 해제됩니다. 경계선 위 클릭은 무시됩니다.");
        var selectedRegions=new HashSet<int>();while(true)
        {
            try
            {
                var picked=ModelBuilder.Meters(app.ActiveUIDocument.Selection.PickPoint($"구역 안쪽 클릭 (현재 {selectedRegions.Count}개) · 완료는 Esc"));var point=Polygons.Factory.CreatePoint(new Coordinate(picked.X,picked.Y));var index=regions.FindIndex(p=>p.Contains(point));
                if(index<0){details.Text="구역 안쪽을 클릭하세요. 경계선 위는 선택되지 않습니다.";continue;}
                if(!selectedRegions.Add(index))selectedRegions.Remove(index);details.Text=$"구역 {selectedRegions.Count}개 선택 · Esc로 완료";
            }
            catch(Autodesk.Revit.Exceptions.OperationCanceledException){break;}
        }
        if(selectedRegions.Count==0){SetStatus("선택된 구역이 없어 생성을 취소했습니다.");return;}var polygons=selectedRegions.Order().Select(i=>regions[i]).ToList();
        var confirm=new TaskDialog("굴착면 생성 확인"){MainInstruction=$"{(kind.SelectedIndex==0?"바닥":"지형솔리드")} {polygons.Count}개 / 합계 {polygons.Sum(p=>p.Area):F2}㎡ / 상단 EL {elevation:F3}m",MainContent=$"레이어: {layer.Text}\n유형: {selectedType.Label}\n선 연결 오차: {tolerance:F1}mm\n면적을 확인하세요. 각 구역은 별도 요소로 생성됩니다.",CommonButtons=TaskDialogCommonButtons.Ok|TaskDialogCommonButtons.Cancel};if(confirm.Show()!=TaskDialogResult.Ok)return;
        var lev=(Level)doc.GetElement(selectedLevel.Id);var created=CreateElements(doc,polygons,kind.SelectedIndex==0,selectedType.Id,lev,elevation);
        app.ActiveUIDocument.Selection.SetElementIds(created);SetStatus($"{created.Count}개 생성: 상단 EL {elevation:F3}m. 요소들이 선택되어 있습니다.");
    }
    internal static List<ElementId> CreateElements(Document doc,IEnumerable<Polygon> polygons,bool floor,ElementId typeId,Level lev,double elevation)
    {
        var created=new List<ElementId>();using var t=new Transaction(doc,"CAD 폐합 경계로 굴착면 작성");t.Start();
        foreach(var p in polygons)
        {
            var loops=ContextBuilder.Loops(p,0,doc.Application.ShortCurveTolerance);Element element=floor?Floor.Create(doc,loops,typeId,lev.Id):Toposolid.Create(doc,loops,typeId,lev.Id);
            var offset=element.get_Parameter(floor?BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM:BuiltInParameter.TOPOSOLID_HEIGHTABOVELEVEL_PARAM);if(offset==null||offset.IsReadOnly||!offset.Set(ModelBuilder.Feet(elevation)-lev.Elevation))throw new InvalidDataException("상단 EL을 설정할 수 없습니다.");created.Add(element.Id);
        }
        doc.Regenerate();foreach(var id in created){var e=doc.GetElement(id);var b=e.get_BoundingBox(null)??throw new InvalidDataException("작성한 요소 형상을 확인할 수 없습니다.");if(Math.Abs(b.Max.Z*.3048-elevation)>.002)throw new InvalidDataException($"요소 {id}: 실제 상단 EL이 요청값과 다릅니다.");}
        if(t.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("요소 생성이 취소되었습니다.");return created;
    }
}
