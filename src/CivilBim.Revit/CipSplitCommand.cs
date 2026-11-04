using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System.Windows;
using System.Windows.Controls;

namespace CivilBim.Revit;

public sealed class CipSplitWindow:ModelessWindow
{
    private readonly HashSet<ElementId> selected=[];
    public CipSplitWindow(Document doc,Settings settings)
    {
        Title="CIP 지층 분할";Width=780;Height=510;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new StackPanel {Margin=new(22)};Content=root;root.Children.Add(new TextBlock {Text="깊이가 맞춰진 CIP → 최근접 시추 지층별 분할",FontSize=21,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        var input=new BoreholePicker(this,root,settings);var count=new TextBlock {Text="분할할 CIP를 선택하세요.",Margin=new(0,12,0,6)};root.Children.Add(count);
        var row=new StackPanel {Orientation=Orientation.Horizontal};root.Children.Add(row);var current=new Button {Content="Revit 현재 선택 가져오기",Padding=new(10,7,10,7)};row.Children.Add(current);var pick=new Button {Content="화면에서 CIP 선택",Padding=new(10,7,10,7),Margin=new(8,0,0,0)};row.Children.Add(pick);
        void Use(IEnumerable<ElementId> ids){selected.Clear();foreach(var id in ids)if(Storage.Info(doc.GetElement(id))?.Kind=="CIP")selected.Add(id);count.Text=$"선택 CIP 요소 {selected.Count}개. 기존 분할 요소를 선택해도 같은 원말뚝 전체를 재분할합니다.";}
        current.Click+=(_,_)=>Api(app=>Use(app.ActiveUIDocument.Selection.GetElementIds()));pick.Click+=(_,_)=>Api(app=>Use(app.ActiveUIDocument.Selection.PickObjects(ObjectType.Element,new CipFilter(),"분할할 CivilBIM CIP를 선택하세요.").Select(r=>r.ElementId)),true);
        var generate=new Button {Content="선택 CIP 지층 분할",Padding=new(12,9,12,9),Margin=new(0,18,0,0)};root.Children.Add(generate);
        generate.Click+=(_,_)=>Api(app=>
        {
            input.Data();if(selected.Count==0)Use(app.ActiveUIDocument.Selection.GetElementIds());if(selected.Count==0)throw new InvalidDataException("CIP를 선택하고 ‘현재 선택 가져오기’ 또는 ‘화면에서 CIP 선택’을 누르세요.");
            var keys=selected.Select(doc.GetElement).Where(e=>e!=null).Select(e=>Storage.Info(e)!).Where(i=>i!=null).Select(i=>(i.Source,i.Batch,i.ParentId)).ToHashSet();settings.BoreholeJson=input.PathBox.Text;WorkflowCommand.Split(doc,settings,selected,notify:false);
            var parts=Storage.Generated(doc).Where(x=>x.Info.Kind=="CIP"&&keys.Contains((x.Info.Source,x.Info.Batch,x.Info.ParentId))).ToList();Use(parts.Select(x=>x.Element.Id));app.ActiveUIDocument.Selection.SetElementIds(selected.ToList());SetStatus($"원말뚝 {keys.Count}개 → 지층 구간 {parts.Count}개. 실제 분할 전후 체적 보존 검사 통과.");
        });
    }
    private sealed class CipFilter:ISelectionFilter {public bool AllowElement(Element e)=>e is FamilyInstance&&Storage.Info(e)?.Kind=="CIP";public bool AllowReference(Reference r,XYZ p)=>false;}
}
[Transaction(TransactionMode.Manual)]
public sealed class CipSplitCommand:IExternalCommand
{
    public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)
    {
        var ui=data.Application.ActiveUIDocument;if(ui==null)return Result.Cancelled;
        ModelessHost.Open(data.Application,"CIP분할",()=>new CipSplitWindow(ui.Document,Storage.Load(ui.Document)));return Result.Succeeded;
    }
}
