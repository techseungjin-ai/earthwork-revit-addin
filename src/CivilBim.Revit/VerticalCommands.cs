using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CivilBim.Core;
using TextBox=System.Windows.Controls.TextBox;
using ComboBox=System.Windows.Controls.ComboBox;

namespace CivilBim.Revit;

public sealed record CadChoice(ImportInstance Instance,string Label) {public override string ToString()=>Label;}
public sealed class VerticalWindow : ModelessWindow
{
    public ImportInstance? Cad {get;private set;}
    public CadReader? Reader {get;private set;}
    public Settings Settings {get;}
    public VerticalWindow(Document doc,Settings settings,string kind)
    {
        Settings=settings;var cip=kind=="CIP";Title=$"CivilBIM · {kind} 일괄 배치";Width=710;Height=720;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new StackPanel {Margin=new(22)};Content=new ScrollViewer {Content=root,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        root.Children.Add(new TextBlock {Text=cip?"CIP 원 중심 인식 → 패밀리 배치":"H-PILE 블록 / H형 선군 인식 → 회전 배치",FontSize=21,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,16)});
        void Label(string text)=>root.Children.Add(new TextBlock {Text=text,Margin=new(0,10,0,5)});
        Label("1. CAD 링크 / 가져오기");var imports=ScreenChoices.Imports(doc);var cad=new ComboBox {ItemsSource=imports,MinHeight=32};ScreenChoices.Cad(this,root,cad,doc);
        if(imports.Count==0)root.Children.Add(new TextBlock {Text="CAD가 목록에 없다면 창을 둔 채 Revit에서 DWG를 링크한 뒤 새로고침 또는 화면 선택을 사용하세요.",TextWrapping=TextWrapping.Wrap,Foreground=System.Windows.Media.Brushes.DarkRed});
        Label("2. 인식할 평면 레이어");var layer=new ComboBox {IsEditable=true,MinHeight=32};ScreenChoices.Layer(this,root,layer,cad,doc);
        Label("3. 배치할 패밀리 유형");var choices=ScreenChoices.Families(doc,ScreenChoices.Vertical);var family=new ComboBox {ItemsSource=choices,MinHeight=32};ScreenChoices.Family(this,root,family,doc,ScreenChoices.Vertical);
        var prior=cip?settings.CipFamily:settings.HPileFamily;family.SelectedItem=choices.FirstOrDefault(c=>c.Id==prior);
        TextBox Field(string label,string value){Label(label);var box=new TextBox {Text=value,Padding=new(6)};root.Children.Add(box);return box;}
        var length=new TextBox {Text=cip?settings.CipLengthParameter:settings.HPileLengthParameter};
        var topParameter=new TextBox {Text=cip?settings.CipTopParameter:settings.HPileTopParameter};


        var extra=cip?null:Field("패밀리 기준각 보정 (도, 보통 0 또는 90)",settings.HPileRotationOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if(cip)root.Children.Add(new TextBlock {Text="CIP 직경과 단면은 선택한 패밀리 유형을 그대로 사용합니다. 유형 치수를 변경하지 않습니다.",TextWrapping=TextWrapping.Wrap,Margin=new(0,10,0,0)});
        root.Children.Add(new TextBlock {Text=$"초기 높이 EL {settings.PileTop:F2} ~ {settings.PileBottom:F2} m · 배치 후 전개도에서 조절",TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,0)});
        var note=new TextBlock {Text="현재 뷰의 자르기 영역이 있으면 그 평면 범위를 사용합니다. 자르기가 없으면 다음 단계에서 생성할 평면 영역을 박스로 지정합니다.",TextWrapping=TextWrapping.Wrap,Margin=new(0,15,0,10)};root.Children.Add(note);
        cad.SelectionChanged+=(_,_)=>Api(()=>
        {
            if(cad.SelectedItem is not CadChoice selected)return;Cad=selected.Instance;Reader=CadReader.Read(doc,Cad);
            layer.ItemsSource=Reader.Layers;layer.Text=cip?settings.CipLayer:settings.HPileLayer;
            if(string.IsNullOrWhiteSpace(layer.Text))layer.Text=Reader.Layers.FirstOrDefault(l=>cip?l.Contains("CIP",StringComparison.OrdinalIgnoreCase)||l.Contains("C.I.P",StringComparison.OrdinalIgnoreCase):l.Contains("PILE",StringComparison.OrdinalIgnoreCase)||l.Contains("멀리언"))??"";
        });
        family.SelectionChanged+=(_,_)=>Api(()=>
        {
            if(family.SelectedItem is not FamilyChoice selected)return;
            var symbol=(FamilySymbol)doc.GetElement(selected.Id);var names=FamilyNames(doc,symbol);
            length.Text=Find(names,"Depth","깊이","길이","Length","높이")??length.Text;topParameter.Text=Find(names,"TOP","상단EL","상단 표고")??"";
        });
        cad.SelectedItem=imports.FirstOrDefault(c=>c.Instance.UniqueId==settings.CadUniqueId)??imports.FirstOrDefault();
        // 선택한 유형의 매핑을 최초에도 제안한다.
        if(family.SelectedItem is FamilyChoice initial)
        {var names=FamilyNames(doc,(FamilySymbol)doc.GetElement(initial.Id));length.Text=Find(names,"Depth","깊이","길이","Length","높이")??length.Text;topParameter.Text=Find(names,"TOP","상단EL","상단 표고")??topParameter.Text;}
        var go=new Button {Content=kind+" 인식 개수 확인 → 일괄 생성",Padding=new(12,10,12,10),Margin=new(0,10,0,0)};root.Children.Add(go);
        go.Click+=(_,_)=>Api(()=>
        {
            try
            {
                if(Cad==null || Reader==null)throw new InvalidDataException("Revit에 CAD를 링크하고 선택하세요.");
                if(family.SelectedItem is not FamilyChoice selected)throw new InvalidDataException("패밀리 유형을 선택하세요.");settings.CadUniqueId=Cad.UniqueId;
                Guard.Finite(settings.PileTop,settings.PileBottom);if(settings.PileTop<=settings.PileBottom)throw new InvalidDataException("저장된 초기 상하단 EL이 올바르지 않습니다.");
                if(cip){settings.CipLayer=layer.Text;settings.CipFamily=selected.Id;settings.CipLengthParameter=length.Text;settings.CipTopParameter=topParameter.Text;settings.CipDiameterParameter="";}
                else {settings.HPileLayer=layer.Text;settings.HPileFamily=selected.Id;settings.HPileLengthParameter=length.Text;settings.HPileTopParameter=topParameter.Text;settings.HPileRotationOffset=Guard.Number(extra!.Text);}
                Accept();
            }catch(Exception ex){SetStatus(ex.Message,true);}
        });
        StickyActions(go);
    }
    internal static string? Find(List<string> names,params string[] candidates)=>candidates.Select(c=>names.FirstOrDefault(n=>n.Equals(c,StringComparison.OrdinalIgnoreCase))).FirstOrDefault(x=>x!=null);
    internal static List<string> FamilyNames(Document doc,FamilySymbol symbol)
    {
        try {using var family=doc.EditFamily(symbol.Family);var names=family.FamilyManager.Parameters.Cast<FamilyParameter>().Where(p=>p.Definition.GetDataType()==SpecTypeId.Length).Select(p=>p.Definition.Name).ToList();family.Close(false);return names;}
        catch{return symbol.Parameters.Cast<Parameter>().Where(p=>p.Definition.GetDataType()==SpecTypeId.Length).Select(p=>p.Definition.Name).ToList();}
    }
}
public static class VerticalService
{
    public static List<FamilyInstance> Place(Document doc,CadReader reader,string kind,Settings s,string source)
    {
        var instances=new List<FamilyInstance>();var batch=Guid.NewGuid().ToString();
        if(kind=="CIP")
        {
            var centers=reader.PileCenters(s.CipLayer,false,1);if(centers.Count==0)throw new InvalidDataException("선택 레이어에서 CIP 원을 찾지 못했습니다.");
            for(var i=0;i<centers.Count;i++)instances.Add(ModelBuilder.Pile(doc,centers[i],new($"CIP-{i+1:D5}","미분할",s.PileTop,s.PileBottom,s.Diameter,""),s,source,batch));
        }
        else
        {
            var symbols=reader.HPiles(s.HPileLayer);if(symbols.Count==0)throw new InvalidDataException("선택 레이어에서 H-PILE 블록 또는 H형 선군을 찾지 못했습니다.");
            foreach(var item in symbols)
            {
                var center=ModelBuilder.Xyz(item.Center);var instance=FamilyBuilder.Vertical(doc,s.HPileFamily,center,s.PileTop,s.PileBottom,s,s.HPileLengthParameter,topParameter:s.HPileTopParameter);
                doc.Regenerate();var sectionSegments=new List<PlanSegment>();
                foreach(var solid in FamilyBuilder.Solids(instance.get_Geometry(new Options{DetailLevel=ViewDetailLevel.Fine})))
                {
                    var topFace=solid.Faces.Cast<Face>().OfType<PlanarFace>().Where(f=>f.FaceNormal.Z>.999).OrderByDescending(f=>f.Origin.Z).FirstOrDefault();
                    if(topFace==null)continue;
                    foreach(EdgeArray loop in topFace.EdgeLoops)foreach(Edge edge in loop)
                    {var curve=edge.AsCurve();if(curve is Line)sectionSegments.Add(new(ModelBuilder.Meters(curve.GetEndPoint(0)),ModelBuilder.Meters(curve.GetEndPoint(1))));}
                }
                var actual=PlanRecognition.HPiles(sectionSegments).SingleOrDefault();
                var angle=item.Angle-(actual?.Angle??0)+s.HPileRotationOffset*Math.PI/180;ElementTransformUtils.RotateElement(doc,instance.Id,Line.CreateBound(center,center+XYZ.BasisZ),angle);
                FamilyBuilder.Track(doc,instance,new ElementInfo("HPile",$"HP-{instances.Count+1:D5}","H-PILE",s.PileTop,s.PileBottom,0,0,s.PileTop-s.PileBottom,"",source,batch,ModelBuilder.SurveyOrLocal(s.Frame,item.Center),s.Frame,s.Scale){LengthParameter=s.HPileLengthParameter,TopParameter=s.HPileTopParameter,Rotation=angle});instances.Add(instance);
            }
        }
        return instances;
    }
    public static Result Execute(ExternalCommandData data,string kind,ref string message)
    {
        var initialUi=data.Application.ActiveUIDocument;if(initialUi==null)return Result.Cancelled;var doc=initialUi.Document;var initialSettings=Storage.Load(doc);
        ModelessHost.Open(data.Application,kind,()=>new VerticalWindow(doc,initialSettings,kind),window=>window.Submitted+=()=>window.Api(app=>
        {
            var ui=app.ActiveUIDocument;
            if(Math.Abs(ui.ActiveView.ViewDirection.Z)<.999)throw new InvalidDataException("평면 뷰에서 부재 영역을 지정하세요.");
            var reader=CadReader.Read(doc,window.Cad??throw new InvalidDataException("CAD를 선택하세요."));
            if(ui.ActiveView is ViewPlan {CropBoxActive:true} plan){var box=plan.CropBox;reader.Crop(box.Transform.OfPoint(box.Min),box.Transform.OfPoint(box.Max));}
            else{var box=ui.Selection.PickBox(Autodesk.Revit.UI.Selection.PickBoxStyle.Directional,"CIP / H-PILE을 생성할 평면 영역을 지정하세요.");reader.Crop(box.Min,box.Max);}
            var s=window.Settings;var count=kind=="CIP"?reader.PileCenters(s.CipLayer,false,1).Count:reader.HPiles(s.HPileLayer).Count;
            var confirm=new TaskDialog(kind+" 일괄 배치"){MainInstruction=$"{kind} {count}개 인식",MainContent="선택 패밀리로 현재 평면 범위에 일괄 배치합니다. 같은 CAD·범위에서 이전에 생성한 동일 부재는 교체합니다.",CommonButtons=TaskDialogCommonButtons.Ok|TaskDialogCommonButtons.Cancel};if(confirm.Show()!=TaskDialogResult.Ok)return;
            var source="CAD:"+window.Cad!.UniqueId+":"+reader.RegionKey;
            using var group=new TransactionGroup(doc,kind+" 배치 및 실제 높이 검산");group.Start();
            using var t=new Transaction(doc,kind+" 평면 일괄 배치");t.Start();
            var old=Storage.Generated(doc).Where(x=>x.Info.Source==source && x.Info.Kind==(kind=="CIP"?"CIP":"HPile")).Select(x=>x.Element.Id).ToList();if(old.Count>0)doc.Delete(old);
            var list=Place(doc,reader,kind,s,source);Storage.Write(doc.ProjectInformation,s);if(t.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("수직부재 배치를 취소했습니다.");
            var released=FamilyBuilder.FinalizeVertical(doc,list,s);group.Assimilate();ui.Selection.SetElementIds(list.Select(i=>i.Id).ToList());window.SetStatus($"{kind} {list.Count}개 배치·실제 높이 검산 완료. 자동결합 {released}건 해제.");
        },true));return Result.Succeeded;
    }
}
[Transaction(TransactionMode.Manual)] public sealed class CipPlacementCommand:IExternalCommand {public Result Execute(ExternalCommandData d,ref string m,ElementSet e)=>VerticalService.Execute(d,"CIP",ref m);}
[Transaction(TransactionMode.Manual)] public sealed class HPilePlacementCommand:IExternalCommand {public Result Execute(ExternalCommandData d,ref string m,ElementSet e)=>VerticalService.Execute(d,"H-PILE",ref m);}
