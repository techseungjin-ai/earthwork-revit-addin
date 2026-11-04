using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CivilBim.Core;
using System.Windows;
using System.Windows.Controls;

namespace CivilBim.Revit;

[Transaction(TransactionMode.Manual)]
public sealed class DevelopmentCommand:IExternalCommand
{
    public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)
    {
        var initial=data.Application.ActiveUIDocument;if(initial==null)return Result.Cancelled;var doc=initial.Document;
        ModelessHost.Open(data.Application,"3D전개도",()=>
        {
            var window=new ModelessWindow {Title="3D 전개도 곡선 → 수직부재 깊이",Width=730,Height=420,WindowStartupLocation=WindowStartupLocation.CenterOwner};var root=new StackPanel {Margin=new(22)};window.Content=root;
            root.Children.Add(new TextBlock {Text="대지 위의 3D 상·하단 모델선으로 깊이를 맞춥니다. 2D 전개도는 별도의 ‘2D 전개도 깊이 대응’을 사용하세요.",TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,12)});
            var targets=initial.Selection.GetElementIds().ToHashSet();IList<Reference> upper=[],lower=[];
            var info=new TextBlock {Text="수직부재와 상단·하단 모델선을 선택하세요.",Margin=new(0,0,0,8),TextWrapping=TextWrapping.Wrap};root.Children.Add(info);
            void Info()=>info.Text=$"대상 선택 {targets.Count}개 / 상단 모델선 {upper.Count}개 / 하단 모델선 {lower.Count}개";
            var use=new Button {Content="Revit에서 선택한 CIP/H-PILE 가져오기",Padding=new(9),Margin=new(0,4,0,4)};root.Children.Add(use);use.Click+=(_,_)=>window.Api(app=>{targets=app.ActiveUIDocument.Selection.GetElementIds().ToHashSet();Info();});
            var top=new Button {Content="화면에서 상단 3D 모델선 선택",Padding=new(9),Margin=new(0,4,0,4)};root.Children.Add(top);top.Click+=(_,_)=>window.Api(app=>{upper=app.ActiveUIDocument.Selection.PickObjects(ObjectType.Element,new CurveFilter(),"상단 3D 모델선을 선택하세요.");Info();},true);
            var bottom=new Button {Content="화면에서 하단 3D 모델선 선택",Padding=new(9),Margin=new(0,4,0,4)};root.Children.Add(bottom);bottom.Click+=(_,_)=>window.Api(app=>{lower=app.ActiveUIDocument.Selection.PickObjects(ObjectType.Element,new CurveFilter(),"하단 3D 모델선을 선택하세요.");Info();},true);
            var apply=new Button {Content="선택 수직부재 깊이 적용",Padding=new(11),Margin=new(0,14,0,0)};root.Children.Add(apply);apply.Click+=(_,_)=>window.Api(app=>
            {
                if(targets.Count==0)targets=app.ActiveUIDocument.Selection.GetElementIds().ToHashSet();var piles=targets.Select(doc.GetElement).OfType<FamilyInstance>().Select(i=>(I:i,Info:Storage.Info(i))).Where(x=>x.Info?.Kind is "CIP" or "HPile").ToList();if(piles.Count==0)throw new InvalidDataException("CIP/H-PILE을 선택하고 현재 선택 가져오기를 누르세요.");
                if(piles.GroupBy(p=>(p.Info!.Source,p.Info.Batch,p.Info.ParentId)).Any(g=>g.Count()>1))throw new InvalidDataException("지층 분할 전의 원말뚝을 선택하세요.");if(upper.Count==0||lower.Count==0)throw new InvalidDataException("상단·하단 모델선을 각각 선택하세요.");
                var settings=Storage.Load(doc);var topLines=Segments(doc,upper);var bottomLines=Segments(doc,lower);var planned=piles.Select(p=>{var local=settings.Frame.ToLocal(p.Info!.Position);return(p.I,Info:p.Info!,Top:ElevationMatching.At(local,topLines,.25)+settings.Frame.Elevation,Bottom:ElevationMatching.At(local,bottomLines,.25)+settings.Frame.Elevation);}).ToList();
                using var t=new Transaction(doc,"3D 전개도 곡선으로 깊이 맞춤");t.Start();foreach(var p in planned)FamilyBuilder.Resize(doc,p.I,p.Info,p.Top,p.Bottom,settings);t.Commit();window.SetStatus($"{planned.Count}개 수직부재 깊이와 실제 상하단 EL 변경 완료.");
            });return window;
        });return Result.Succeeded;
    }
    public static List<PlanSegment> Segments(Document doc,IList<Reference> references)
    {
        var output=new List<PlanSegment>();foreach(var r in references){var curve=doc.GetElement(r) as CurveElement??throw new InvalidDataException("선택한 모델선이 삭제되었습니다. 다시 선택하세요.");var points=curve.GeometryCurve.Tessellate();for(var i=1;i<points.Count;i++)output.Add(new(ModelBuilder.Meters(points[i-1]),ModelBuilder.Meters(points[i])));}return output;
    }
    private sealed class CurveFilter:ISelectionFilter {public bool AllowElement(Element e)=>e is ModelCurve;public bool AllowReference(Reference r,XYZ p)=>false;}
}
