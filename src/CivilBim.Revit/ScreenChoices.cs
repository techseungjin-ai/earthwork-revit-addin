using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System.Windows;
using System.Windows.Controls;
using Panel=System.Windows.Controls.Panel;
using ComboBox=System.Windows.Controls.ComboBox;

namespace CivilBim.Revit;

public static class ScreenChoices
{
    public static List<CadChoice> Imports(Document doc)=>new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Select(i=>new CadChoice(i,doc.GetElement(i.GetTypeId())?.Name??i.Name)).OrderBy(i=>i.Label).ToList();
    public static void Cad(ModelessWindow owner,Panel panel,ComboBox box,Document doc)
    {
        var row=new DockPanel {Margin=new(0,2,0,0)};
        var pick=new Button {Content="화면에서 선택",Padding=new(8,5,8,5),Margin=new(6,0,0,0)};DockPanel.SetDock(pick,Dock.Right);row.Children.Add(pick);
        var refresh=new Button {Content="새로고침",Padding=new(6),Margin=new(6,0,0,0)};DockPanel.SetDock(refresh,Dock.Right);row.Children.Add(refresh);row.Children.Add(box);panel.Children.Add(row);
        void Reload(string? id=null){var old=id??(box.SelectedItem as CadChoice)?.Instance.UniqueId;var values=Imports(doc);box.ItemsSource=values;box.SelectedItem=values.FirstOrDefault(c=>c.Instance.UniqueId==old);}
        refresh.Click+=(_,_)=>owner.Api(()=>Reload());
        pick.Click+=(_,_)=>owner.Api(app=>{var reference=app.ActiveUIDocument.Selection.PickObject(ObjectType.Element,new CadFilter(),"CAD 링크 / 가져오기를 선택하세요.");Reload(doc.GetElement(reference).UniqueId);},true);
    }
    public static void Layer(ModelessWindow owner,Panel panel,ComboBox layer,ComboBox cad,Document doc)
    {
        var row=new DockPanel {Margin=new(0,2,0,0)};var pick=new Button {Content="화면에서 선 선택",Padding=new(8,5,8,5),Margin=new(6,0,0,0)};DockPanel.SetDock(pick,Dock.Right);row.Children.Add(pick);row.Children.Add(layer);panel.Children.Add(row);
        pick.Click+=(_,_)=>owner.Api(app=>
        {
            var reference=app.ActiveUIDocument.Selection.PickObject(ObjectType.PointOnElement,new CadFilter(),"인식할 레이어의 CAD 선·원 위를 클릭하세요.");
            var import=(ImportInstance)doc.GetElement(reference);var values=Imports(doc);cad.ItemsSource=values;cad.SelectedItem=values.First(c=>c.Instance.Id==import.Id);
            var reader=CadReader.Read(doc,import);var name=ResolveLayer(doc,import,reference,reader);layer.ItemsSource=reader.Layers;layer.Text=name;owner.SetStatus("선택 레이어: "+name);
        },true);
    }
    public static string ResolveLayer(Document doc,ImportInstance import,Reference reference,CadReader reader)
    {
        var geometry=import.GetGeometryObjectFromReference(reference);var style=geometry==null?null:doc.GetElement(geometry.GraphicsStyleId) as GraphicsStyle;var direct=style?.GraphicsStyleCategory?.Name;
        if(direct!=null&&reader.Layers.Contains(direct))return direct;
        var point=reference.GlobalPoint??throw new InvalidDataException("CAD 선 위를 클릭하세요. 레이어를 확인할 수 없습니다.");
        return NearestLayer(reader,point);
    }
    public static string NearestLayer(CadReader reader,XYZ point)
    {
        var candidates=new List<(string Layer,double Distance)>();
        foreach(var path in reader.Paths)for(var i=1;i<path.Points.Count;i++)
        {var a=path.Points[i-1];var b=path.Points[i];var d=b-a;var length=d.DotProduct(d);if(length<1e-12)continue;var ratio=Math.Clamp((point-a).DotProduct(d)/length,0,1);candidates.Add((path.Layer,point.DistanceTo(a+d.Multiply(ratio))));}
        foreach(var circle in reader.Circles)candidates.Add((circle.Layer,Math.Abs(point.DistanceTo(circle.Center)-circle.Radius)));
        var nearest=candidates.OrderBy(c=>c.Distance).FirstOrDefault();if(nearest.Layer==null||nearest.Distance>ModelBuilder.Feet(.02))throw new InvalidDataException("선택 위치의 레이어를 찾지 못했습니다. 선을 확대해 다시 선택하거나 드롭다운을 사용하세요.");
        if(candidates.Where(c=>Math.Abs(c.Distance-nearest.Distance)<1e-5).Select(c=>c.Layer).Distinct().Count()>1)throw new InvalidDataException("여러 레이어의 선이 겹칩니다. 다른 선 위를 선택하세요.");return nearest.Layer;
    }
    public static bool Vertical(FamilySymbol f)=>f.Category.Id.Value!=(long)BuiltInCategory.OST_CurtainWallMullions&&f.Category.Id.Value!=(long)BuiltInCategory.OST_CurtainWallPanels&&(f.Family.FamilyPlacementType==FamilyPlacementType.OneLevelBased||(f.Family.FamilyPlacementType==FamilyPlacementType.TwoLevelsBased&&f.Category.Id.Value==(long)BuiltInCategory.OST_StructuralColumns));
    public static bool Horizontal(FamilySymbol f)=>f.Family.FamilyPlacementType is FamilyPlacementType.CurveBased or FamilyPlacementType.CurveDrivenStructural;
    public static FamilyChoice[] Families(Document doc,Func<FamilySymbol,bool> accept)=>FamilyBuilder.Choices(doc).Where(c=>accept((FamilySymbol)doc.GetElement(c.Id))).ToArray();
    public static void Family(ModelessWindow owner,Panel panel,ComboBox box,Document doc,Func<FamilySymbol,bool> accept)
    {
        var row=new DockPanel {Margin=new(0,2,0,0)};var pick=new Button {Content="화면에서 선택",Padding=new(8,5,8,5),Margin=new(6,0,0,0)};DockPanel.SetDock(pick,Dock.Right);row.Children.Add(pick);
        var refresh=new Button {Content="새로고침",Padding=new(6),Margin=new(6,0,0,0)};DockPanel.SetDock(refresh,Dock.Right);row.Children.Add(refresh);row.Children.Add(box);panel.Children.Add(row);
        void Reload(string? id=null){var old=id??(box.SelectedItem as FamilyChoice)?.Id;var values=Families(doc,accept);box.ItemsSource=values;box.SelectedItem=values.FirstOrDefault(c=>c.Id==old);}
        refresh.Click+=(_,_)=>owner.Api(()=>Reload());
        pick.Click+=(_,_)=>owner.Api(app=>{var r=app.ActiveUIDocument.Selection.PickObject(ObjectType.Element,new FamilyFilter(),"사용할 패밀리 인스턴스를 선택하세요.");var family=(FamilyInstance)doc.GetElement(r);if(!accept(family.Symbol))throw new InvalidDataException("이 패밀리의 배치 방식은 해당 명령에서 지원하지 않습니다.");Reload(family.Symbol.UniqueId);},true);
    }
    private sealed class CadFilter:ISelectionFilter {public bool AllowElement(Element e)=>e is ImportInstance;public bool AllowReference(Reference r,XYZ p)=>true;}
    private sealed class FamilyFilter:ISelectionFilter {public bool AllowElement(Element e)=>e is FamilyInstance;public bool AllowReference(Reference r,XYZ p)=>false;}
}
