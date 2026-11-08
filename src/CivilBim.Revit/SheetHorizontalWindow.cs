using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CivilBim.Core;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ComboBox=System.Windows.Controls.ComboBox;
using TextBox=System.Windows.Controls.TextBox;

namespace CivilBim.Revit;

// Explicit center-height guide lines avoid interpreting CAD outlines as two different tiers.
public static class SheetTierHeights
{
    public static List<double> Read(Document doc,View source,IEnumerable<ElementId> ids,double elevation)
    {
        Transform map;
        if(source is ViewSheet sheet)
        {
            var views=sheet.GetAllViewports().Select(id=>(Viewport)doc.GetElement(id)).Where(v=>SectionSheetData.Read((View)doc.GetElement(v.ViewId))!=null).ToList();
            if(views.Count==0)throw new InvalidDataException("경로 단면이 배치된 시트에서 단별 중심 높이선을 선택하세요.");
            var maps=views.Select(v=>SectionSheetData.ToSheet((View)doc.GetElement(v.ViewId),v)).ToList();map=maps[0];
            double Height(Transform m,XYZ p){var up=m.OfVector(XYZ.BasisZ);return (p-m.OfPoint(XYZ.Zero)).DotProduct(up)/up.DotProduct(up)*.3048+elevation;}
            if(maps.Skip(1).Any(m=>Math.Abs(Height(m,XYZ.Zero)-Height(map,XYZ.Zero))>.002||Math.Abs(Height(m,XYZ.BasisY)-Height(map,XYZ.BasisY))>.002))throw new InvalidDataException("시트 단면들의 높이축 또는 축척이 다릅니다. 해당 단면을 활성화한 뒤 기준선을 선택하세요.");
        }
        else if(source is ViewSection&&SectionSheetData.Read(source)!=null)map=Transform.Identity;
        else throw new InvalidDataException("‘경로 단면·시트’로 만든 단면 또는 시트에서 높이선을 선택하세요.");
        var result=new List<double>();var upVector=map.OfVector(XYZ.BasisZ);var origin=map.OfPoint(XYZ.Zero);
        foreach(var id in ids)
        {
            var curve=doc.GetElement(id) as CurveElement??throw new InvalidDataException("Revit 모델선/상세선을 선택하세요.");
            if(curve is DetailCurve&&curve.OwnerViewId!=source.Id)throw new InvalidDataException("선택한 상세선의 뷰가 다릅니다. 다시 선택하세요.");
            var points=curve.GeometryCurve.Tessellate();
            if(source is ViewSection&&points.Any(p=>Math.Abs((p-source.Origin).DotProduct(source.ViewDirection))>ModelBuilder.Feet(.02)))throw new InvalidDataException("기준선은 활성 단면의 작업면에 그리세요.");
            var heights=points.Select(p=>(p-origin).DotProduct(upVector)/upVector.DotProduct(upVector)*.3048+elevation).ToList();
            if(heights.Max()-heights.Min()>.002)throw new InvalidDataException("단별 중심 높이선은 수평으로 그리세요. 경사진 부재는 별도 배치가 필요합니다.");
            var height=heights.Average();if(!result.Any(z=>Math.Abs(z-height)<.002))result.Add(height);
        }
        if(result.Count==0)throw new InvalidDataException("높이선을 먼저 선택하세요.");return result.OrderDescending().ToList();
    }
}

public sealed class SheetHorizontalWindow:ModelessWindow
{
    private readonly Document doc;
    private readonly bool pointSupports;
    private readonly ComboBox supportKind=new(){ItemsSource=new[]{"어스앵커","락볼트"},SelectedIndex=0,MinHeight=30};
    private string? PointKind=>pointSupports?(string)supportKind.SelectedItem:null;
    private readonly TextBox offset=new(){Text="0",MinHeight=30},rotation=new(){Text="0",MinHeight=30};
    private readonly ComboBox endpoint=new(){ItemsSource=new[]{"CAD 끝점","CAD 시작점"},SelectedIndex=0,MinHeight=30};
    private string MemberName=>pointSupports?"어스앵커 · 락볼트":"STRUT · 띠장";
    private readonly ComboBox cad=new(){MinHeight=30},layer=new(){IsEditable=true,MinHeight=30},family=new(){MinHeight=30};
    private List<ElementId> guideIds=[];private ElementId guideView=ElementId.InvalidElementId;
    private List<PlanSegment>? reviewed;private string signature="",sourceKey="";
    private readonly TextBlock summary=new(){TextWrapping=TextWrapping.Wrap,Margin=new(0,12,0,12)};
    public SheetHorizontalWindow(Document document,Settings settings,bool pointSupports=false)
    {
        doc=document;this.pointSupports=pointSupports;Title=MemberName+" — 전개도 높이로 배치";Width=760;Height=660;MaxHeight=SystemParameters.WorkArea.Height-35;
        var panel=new StackPanel{Margin=new(22)};Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        panel.Children.Add(new TextBlock{Text=$"평면 CAD + 전개도 단별 높이 → {MemberName} 배치",FontSize=21,TextWrapping=TextWrapping.Wrap});
        void Label(string text)=>panel.Children.Add(new TextBlock{Text=text,Margin=new(0,14,0,5)});
        Button Add(string text,Action<UIApplication> action,bool pick=false){var b=new Button{Content=text,Padding=new(10),Margin=new(0,5,0,5)};panel.Children.Add(b);b.Click+=(_,_)=>Api(action,pick);return b;}
        Label($"1. {MemberName} 평면 CAD");cad.ItemsSource=ScreenChoices.Imports(doc);ScreenChoices.Cad(this,panel,cad,doc);
        Label("평면 중심선 레이어 (영역 일괄 검토 시 필요)");ScreenChoices.Layer(this,panel,layer,cad,doc,"선에서 레이어 가져오기");
        cad.SelectionChanged+=(_,_)=>Api(()=>{if(cad.SelectedItem is CadChoice c){layer.ItemsSource=CadReader.Read(doc,c.Instance).Layers;layer.Text=pointSupports?"":settings.StrutLayer;}});
        if(pointSupports){Label("부재 종류");panel.Children.Add(supportKind);}
        Func<FamilySymbol,bool> accept=pointSupports?FamilyBuilder.IsPointGeneric:ScreenChoices.Horizontal;
        Label("2. 배치할 패밀리 유형");family.ItemsSource=ScreenChoices.Families(doc,accept);
        if(!pointSupports)family.SelectedItem=((FamilyChoice[])family.ItemsSource).FirstOrDefault(f=>f.Id==settings.StrutFamily);
        ScreenChoices.Family(this,panel,family,doc,accept);
        if(pointSupports)
        {
            Label("삽입점 기준");panel.Children.Add(endpoint);
            Label("선 끝에서 반대쪽 끝을 향한 오프셋 (mm)");panel.Children.Add(offset);
            Label("선 방향 기준 평면 회전 보정 (°)");panel.Children.Add(rotation);
        }
        Label("3. 전개도에 겹쳐 그린 단별 중심 높이선");
        panel.Children.Add(new TextBlock{Text="경로 단면 시트에 전개도를 맞춘 뒤 각 단의 중심 높이에 Revit 상세선을 그리세요. 단면을 활성화한 경우 모델선/상세선도 사용할 수 있습니다. 선택한 높이는 검토한 평면 부재 전체에 적용됩니다.",TextWrapping=TextWrapping.Wrap});
        Add("시트 / 단면에서 높이선 여러 개 선택 → 완료",app=>{TaskDialog.Show("단별 높이선 선택","경로 단면 시트 또는 활성 단면에서 각 단의 중심 높이선을 선택하고 완료를 누르세요. CAD 윤곽선 대신 Revit 선을 사용합니다.");var ui=app.ActiveUIDocument;var refs=ui.Selection.PickObjects(ObjectType.Element,new GuideFilter(),"단별 중심 높이선 선택 후 완료");var values=refs.Select(r=>r.ElementId).Distinct().ToList();var levels=SheetTierHeights.Read(doc,ui.ActiveView,values,Storage.Load(doc).Frame.Elevation);guideIds=values;guideView=ui.ActiveView.Id;reviewed=null;summary.Text=$"{levels.Count}단 / EL {string.Join(", ",levels.Select(z=>z.ToString("F3")))}m. 평면 뷰로 돌아가 배치 결과를 검토하세요.";},true);
        panel.Children.Add(summary);
        var direct=Add("4. 배치할 CAD 중심선 직접 선택 → 완료",ReviewSelected,true);
        var review=Add("4. 또는 영역 안 같은 레이어 일괄 검토",Review,true);
        var apply=Add($"5. 검토한 {MemberName} 생성 / 갱신",app=>Apply(app.ActiveUIDocument));apply.Background=System.Windows.Media.Brushes.RoyalBlue;apply.Foreground=System.Windows.Media.Brushes.White;StickyActions(direct,review,apply);
    }
    private sealed class GuideFilter:ISelectionFilter{public bool AllowElement(Element e)=>e is CurveElement;public bool AllowReference(Reference r,XYZ p)=>false;}
    private (CadChoice Cad,FamilyChoice Family,List<double> Levels) Input()
    {
        var c=cad.SelectedItem as CadChoice??throw new InvalidDataException("평면 CAD를 선택하세요.");var f=family.SelectedItem as FamilyChoice??throw new InvalidDataException("배치할 패밀리 유형을 선택하세요.");
        var view=doc.GetElement(guideView) as View??throw new InvalidDataException("전개도 단별 높이선을 먼저 선택하세요.");
        return(c,f,SheetTierHeights.Read(doc,view,guideIds,Storage.Load(doc).Frame.Elevation));
    }
    private string Fingerprint((CadChoice Cad,FamilyChoice Family,List<double> Levels) input)=>JsonSerializer.Serialize(new
    {
        Cad=input.Cad.Instance.UniqueId,Layer=layer.Text,Family=input.Family.Id,Kind=PointKind,Endpoint=endpoint.SelectedIndex,Offset=offset.Text,
        Rotation=rotation.Text,Frame=Storage.Load(doc).Frame,input.Levels,
        Paths=CadReader.Read(doc,input.Cad.Instance).Paths.Where(p=>p.Layer==layer.Text).Select(p=>new{p.Closed,Points=p.Points.Select(ModelBuilder.Meters).ToList()}).ToList()
    });
    private sealed class SelectedCadFilter(ElementId id):ISelectionFilter
    {
        public bool AllowElement(Element e)=>e.Id==id;
        public bool AllowReference(Reference r,XYZ p)=>r.ElementId==id;
    }
    private void ReviewSelected(UIApplication app)
    {
        reviewed=null;
        var input=Input();
        var ui=app.ActiveUIDocument;
        if(ui.ActiveView is not ViewPlan)throw new InvalidDataException("평면 뷰로 돌아온 뒤 중심선을 선택하세요.");
        var refs=ui.Selection.PickObjects(ObjectType.PointOnElement,new SelectedCadFilter(input.Cad.Instance.Id),"배치할 중심선 위를 클릭하고 완료를 누르세요.");
        if(refs.Count==0)throw new InvalidDataException("배치할 CAD 중심선을 선택하세요.");
        var reader=CadReader.Read(doc,input.Cad.Instance);
        var layers=refs.Select(r=>ScreenChoices.ResolveLayer(doc,input.Cad.Instance,r,reader)).Distinct().ToList();
        if(layers.Count!=1)throw new InvalidDataException("한 번에 같은 레이어의 CAD 중심선을 선택하세요. 다른 레이어는 따로 배치할 수 있습니다.");
        var selectedLayer=layers[0];
        var candidates=DirectCandidates(reader,selectedLayer,pointSupports);
        var lines=refs.Select(r=>HorizontalRecognition.PickSegment(candidates,ModelBuilder.Meters(r.GlobalPoint??throw new InvalidDataException("CAD 중심선 위를 클릭하세요."))))
            .DistinctBy(HorizontalRecognition.SegmentKey).ToList();
        layer.Text=selectedLayer;
        var key=string.Join(";",lines.Select(HorizontalRecognition.SegmentKey).Order(StringComparer.Ordinal));
        ReviewLines(input,lines,"DIRECT:"+key,"직접 선택");
    }
    internal static List<PlanSegment> DirectCandidates(CadReader reader,string layer,bool pointSupports)=>
        reader.Paths.Where(p=>p.Layer==layer&&!p.Closed&&(!pointSupports||p.Points.Count==2))
            .SelectMany(p=>p.Points.Zip(p.Points.Skip(1),(a,b)=>new PlanSegment(ModelBuilder.Meters(a),ModelBuilder.Meters(b)))).ToList();
    private void Review(UIApplication app)
    {
        reviewed=null;var input=Input();if(app.ActiveUIDocument.ActiveView is not ViewPlan)throw new InvalidDataException("평면 뷰로 돌아온 뒤 배치 영역을 지정하세요.");
        if(string.IsNullOrWhiteSpace(layer.Text))throw new InvalidDataException("영역 일괄 검토에 사용할 평면 중심선 레이어를 선택하세요.");
        TaskDialog.Show("배치 영역",$"평면에서 {MemberName}을 생성할 영역을 드래그하세요. 선택한 레이어의 부재를 모든 선택 단에 배치합니다.");var box=app.ActiveUIDocument.Selection.PickBox(PickBoxStyle.Directional,"평면 배치 영역 지정");
        var reader=CadReader.Read(doc,input.Cad.Instance);reader.Crop(box.Min,box.Max);
        var paths=reader.Paths.Where(p=>p.Layer==layer.Text);
        var lines=!pointSupports?HorizontalRecognition.Centerlines(paths.Select(p=>new PlanPath(p.Points.Select(ModelBuilder.Meters).ToList(),p.Closed))):
            paths.Where(p=>!p.Closed&&p.Points.Count==2).Select(p=>new PlanSegment(ModelBuilder.Meters(p.Points[0]),ModelBuilder.Meters(p.Points[1]))).Where(l=>l.Length>.001).Distinct().ToList();
        ReviewLines(input,lines,reader.RegionKey,"영역 일괄");
    }
    private void ReviewLines((CadChoice Cad,FamilyChoice Family,List<double> Levels) input,List<PlanSegment> lines,string key,string method)
    {
        if(lines.Count==0)throw new InvalidDataException("배치할 중심선이 없습니다.");
        if(pointSupports)
        {
            var distance=Guard.Number(offset.Text)/1000;
            Guard.Finite(distance,Guard.Number(rotation.Text));
            if(distance<0||lines.Any(l=>distance>l.Length+1e-9))throw new InvalidDataException("오프셋은 0부터 가장 짧은 배치선의 길이까지 입력하세요.");
        }
        signature=Fingerprint(input);sourceKey=(pointSupports?"SHEET-"+PointKind:"SHEET-STRUT")+":"+input.Cad.Instance.UniqueId+":"+layer.Text+":"+key;reviewed=lines;
        summary.Text=$"{method} {lines.Count}개 × {input.Levels.Count}단 = {lines.Count*input.Levels.Count}개\nEL {string.Join(", ",input.Levels.Select(z=>z.ToString("F3")))}m\n패밀리 {input.Family.Label}\n아직 생성하지 않았습니다. 아래 생성 / 갱신을 누르세요.";
    }
    private void Apply(UIDocument ui)
    {
        if(reviewed==null)throw new InvalidDataException("4번에서 배치 결과를 먼저 검토하세요.");
        var input=Input();var settings=Storage.Load(doc);var pointKind=PointKind;
        if(signature!=Fingerprint(input))throw new InvalidDataException("입력이 변경됐습니다. 4번에서 배치 결과를 다시 검토하세요.");
        using var t=new Transaction(doc,$"전개도 높이 기준 {MemberName} 배치");t.Start();var prior=Storage.Generated(doc).Where(g=>g.Info.Kind==(pointKind??"Strut")&&g.Info.Source==sourceKey).Select(g=>g.Element.Id).ToList();if(prior.Count>0)doc.Delete(prior);
        var placed=pointKind==null?HorizontalService.Place(doc,reviewed,input.Levels,input.Family.Id,settings,sourceKey):
            HorizontalService.PlacePoints(doc,reviewed,input.Levels,input.Family.Id,settings,sourceKey,pointKind,Guard.Number(offset.Text)/1000,endpoint.SelectedIndex==0,Guard.Number(rotation.Text));
        if(pointKind==null){settings.StrutFamily=input.Family.Id;settings.StrutLayer=layer.Text;Storage.Write(doc.ProjectInformation,settings);}
        if(t.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("수평부재 생성을 취소했습니다.");
        ui.Selection.SetElementIds(placed.Select(e=>e.Id).ToList());
        summary.Text=$"{reviewed.Count}개 × {input.Levels.Count}단 = {placed.Count}개 배치 완료";
        SetStatus($"생성한 {placed.Count}개를 선택했습니다. 같은 대상에 재적용하면 해당 배치를 갱신합니다.");
    }
}
