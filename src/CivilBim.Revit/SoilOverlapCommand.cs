using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CivilBim.Core;
using System.Windows;
using System.Windows.Controls;

namespace CivilBim.Revit;

public sealed record SoilOverlapRow(string Layer,string Element,double Before,double After,double Cut,double Fill)
{public double Difference=>Before-After;}
public sealed record SoilIntersection(string Element,string Layer,double Volume,double Total,string Signature);
public static class SoilOverlap
{
    public static SoilIntersection CalculateSingle(Toposolid terrain,Element geology)
    {
        var info=Storage.Info(geology);if(info?.Kind!="SiteSoil")throw new InvalidDataException("시추 기반 지층 일반모델을 선택하세요.");
        if(terrain.HostTopoId!=ElementId.InvalidElementId)throw new InvalidDataException("도로 구획이 아닌 원본 지형을 선택하세요.");
        var solids=Read(geology);var box=FamilyBuilder.Bounds(geology);var min=box.Min-new XYZ(.01,.01,.01);var max=box.Max+new XYZ(.01,.01,.01);
        var clip=GeometryCreationUtilities.CreateExtrusionGeometry(new[]{ModelBuilder.Loop(new[]{min,new XYZ(max.X,min.Y,min.Z),new XYZ(max.X,max.Y,min.Z),new XYZ(min.X,max.Y,min.Z)})},XYZ.BasisZ,max.Z-min.Z);
        var clipped=Intersect(Read(terrain),[clip]);var volume=Intersect(solids,clipped).Sum(ModelBuilder.Volume);var total=solids.Sum(ModelBuilder.Volume);
        if(volume>total+Math.Max(.001,total*1e-5))throw new InvalidDataException("중복 체적이 지층 전체체적보다 큽니다. 겹친 솔리드가 있는지 확인하세요.");
        var points=solids.SelectMany(s=>s.Edges.Cast<Edge>().SelectMany(e=>e.Tessellate())).Select(p=>FormattableString.Invariant($"{p.X:F8},{p.Y:F8},{p.Z:F8}")).Distinct().Order(StringComparer.Ordinal);
        var signature=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(FormattableString.Invariant($"{total:R}|")+string.Join(";",points))));
        return new(geology.UniqueId,info.Layer,volume,total,signature);
    }
    public static List<Solid> Read(Element element)
    {
        var geometry=element.get_Geometry(new Options {DetailLevel=ViewDetailLevel.Fine,IncludeNonVisibleObjects=true})??throw new InvalidDataException($"{element.Id}: 솔리드가 없습니다.");
        var solids=FamilyBuilder.Solids(geometry).Where(s=>s.Volume>1e-9).ToList();if(solids.Count==0)throw new InvalidDataException($"{element.Id}: 체적이 있는 솔리드가 없습니다.");return solids;
    }
    private static bool Overlap(Solid a,Solid b)
    {
        var aa=a.GetBoundingBox();var bb=b.GetBoundingBox();
        // Revit Solid bounding boxes may carry their own local transform.
        static (XYZ Min,XYZ Max) Bounds(BoundingBoxXYZ box){var p=(from x in new[]{box.Min.X,box.Max.X} from y in new[]{box.Min.Y,box.Max.Y} from z in new[]{box.Min.Z,box.Max.Z} select box.Transform.OfPoint(new XYZ(x,y,z))).ToList();return(new(p.Min(v=>v.X),p.Min(v=>v.Y),p.Min(v=>v.Z)),new(p.Max(v=>v.X),p.Max(v=>v.Y),p.Max(v=>v.Z)));}
        var x=Bounds(aa);var y=Bounds(bb);return x.Min.X<y.Max.X-1e-8&&x.Max.X>y.Min.X+1e-8&&x.Min.Y<y.Max.Y-1e-8&&x.Max.Y>y.Min.Y+1e-8&&x.Min.Z<y.Max.Z-1e-8&&x.Max.Z>y.Min.Z+1e-8;
    }
    private static List<Solid> Intersect(List<Solid> a,List<Solid> b)
    {
        var result=new List<Solid>();foreach(var x in a)foreach(var y in b)if(Overlap(x,y)){var solid=BooleanOperationsUtils.ExecuteBooleanOperation(x,y,BooleanOperationsType.Intersect);if(solid.Volume>1e-9)result.Add(solid);}return result;
    }
    private static double Remaining(List<Solid> source,List<Solid> remove)
    {
        double volume=0;foreach(var piece in source){var current=piece;foreach(var cutter in remove){if(current.Volume<1e-9)break;if(Overlap(current,cutter))current=BooleanOperationsUtils.ExecuteBooleanOperation(current,cutter,BooleanOperationsType.Difference);}volume+=ModelBuilder.Volume(current);}return volume;
    }
    public static List<SoilOverlapRow> Calculate(Toposolid before,Toposolid after,IEnumerable<Element> geology)
    {
        if(before.Id==after.Id)throw new InvalidDataException("현황과 굴착 후 지형은 서로 다른 요소여야 합니다.");
        if(before.HostTopoId!=ElementId.InvalidElementId||after.HostTopoId!=ElementId.InvalidElementId)throw new InvalidDataException("도로 구획이 아닌 원본 지형 솔리드 두 개를 선택하세요.");
        var selected=geology.Where(e=>Storage.Info(e)?.Kind=="SiteSoil").ToList();if(selected.Count==0)throw new InvalidDataException("CivilBIM 작업대지 지층을 선택하세요.");
        var bounds=selected.Select(e=>e.get_BoundingBox(null)??throw new InvalidDataException("지층 경계를 읽을 수 없습니다.")).SelectMany(b=>from x in new[]{b.Min.X,b.Max.X} from y in new[]{b.Min.Y,b.Max.Y} from z in new[]{b.Min.Z,b.Max.Z} select b.Transform.OfPoint(new XYZ(x,y,z))).ToList();
        const double margin=.01;var min=new XYZ(bounds.Min(p=>p.X)-margin,bounds.Min(p=>p.Y)-margin,bounds.Min(p=>p.Z)-margin);var max=new XYZ(bounds.Max(p=>p.X)+margin,bounds.Max(p=>p.Y)+margin,bounds.Max(p=>p.Z)+margin);
        var clip=GeometryCreationUtilities.CreateExtrusionGeometry(new[]{ModelBuilder.Loop(new[]{min,new XYZ(max.X,min.Y,min.Z),new XYZ(max.X,max.Y,min.Z),new XYZ(min.X,max.Y,min.Z)})},XYZ.BasisZ,max.Z-min.Z);
        // Clip large terrain once to the geological worksite before intersecting individual wedges.
        var original=Intersect(Read(before),[clip]);var proposed=Intersect(Read(after),[clip]);var result=new List<SoilOverlapRow>();
        var geologicalBottom=bounds.Min(p=>p.Z);
        foreach(var solid in proposed)foreach(Face face in solid.Faces)
        {
            var uv=face.GetBoundingBox();if(face.ComputeNormal(new UV((uv.Min.U+uv.Max.U)/2,(uv.Min.V+uv.Max.V)/2)).Z>=-1e-6)continue;
            var mesh=face.Triangulate();foreach(var vertex in mesh.Vertices)if(vertex.Z>geologicalBottom+ModelBuilder.Feet(.002))throw new InvalidDataException("굴착 후 지형의 하부면이 지층 범위보다 높습니다. 지형 두께/하부면의 변화가 굴착량에 섞이지 않도록 현황·굴착후 지형 모두 조사심도 아래까지 충분히 두껍게 만드세요.");
        }
        foreach(var element in selected)
        {
            var info=Storage.Info(element);if(info?.Kind!="SiteSoil")continue;
            try
            {
                var solids=Read(element);var pre=Intersect(solids,original);var post=Intersect(solids,proposed);var total=solids.Sum(ModelBuilder.Volume);var v0=pre.Sum(ModelBuilder.Volume);var v1=post.Sum(ModelBuilder.Volume);
                if(total-v0>Math.Max(.01,total*.0001))throw new InvalidDataException($"현황 지형이 {info.Layer} 지층의 {v0/total:P1}만 포함합니다. 현황 지형의 바닥이 조사심도보다 충분히 낮은지, 지층과 위치/범위가 같은지 확인하세요. 얇은 지형의 중복값을 굴착량으로 확정하지 않습니다.");
                var cut=Remaining(pre,proposed);var fill=Remaining(post,original);
                if(Math.Abs((v0-v1)-(cut-fill))>Math.Max(.01,total*.0001))throw new InvalidDataException("현황−굴착후 차이와 절토−성토 검산이 일치하지 않습니다.");
                result.Add(new(info.Layer,element.UniqueId,v0,v1,cut,fill));
            }
            catch(Exception ex){throw new InvalidDataException($"지층 {info.Layer} / 요소 {element.Id}: 중복체적 계산 중단. 결과를 저장하지 않았습니다.\n{ex.Message}",ex);}
        }
        if(result.Count==0)throw new InvalidDataException("CivilBIM 작업대지 지층을 선택하세요.");return result;
    }
}
public sealed class SoilOverlapWindow:ModelessWindow
{
    private readonly Document doc;
    public SoilOverlapWindow(Document document)
    {
        doc=document;Title="지형 · 지층 중복체적 기록";Width=660;Height=390;
        var root=new StackPanel {Margin=new(22)};Content=root;
        root.Children.Add(new TextBlock {Text="지형 선택 → 지층 선택 → 중복체적 기록",FontSize=22,TextWrapping=TextWrapping.Wrap});
        root.Children.Add(new TextBlock {Text="현황에서 한 번 기록하고, 굴착 후 같은 지층에 다시 기록하세요. 지층 파라미터에 두 중복체적과 차이(m³)를 저장합니다. 지형이 충분히 두꺼워야 깊은 지층 전체와 겹칩니다.",TextWrapping=TextWrapping.Wrap,Margin=new(0,15,0,15)});
        void Add(string label,bool baseline){var button=new Button {Content=label,Padding=new(12),Margin=new(0,6,0,6)};root.Children.Add(button);button.Click+=(_,_)=>Api(app=>Record(app,baseline),true);}
        Add("현황 중복체적 기록 (굴착 전)",true);Add("굴착후 중복체적 기록 · 현황과 차이 계산",false);
        root.Children.Add(new TextBlock {Text="매립토·풍화토·암층을 하나씩 또는 여러 개 선택할 수 있습니다. 값을 확인하려면 해당 지층의 속성이나 ‘04_지층별 현황·굴착후 중복 체적’ 일람표를 여세요.",TextWrapping=TextWrapping.Wrap,Margin=new(0,14,0,0)});
    }
    private void Record(UIApplication app,bool baseline)
    {
        var ui=app.ActiveUIDocument;
        TaskDialog.Show("CivilBIM · 1/2",baseline?"굴착 전 현황 지형을 클릭하세요.":"굴착 후 지형을 클릭하세요.");
        var terrain=(Toposolid)doc.GetElement(ui.Selection.PickObject(ObjectType.Element,new TopoFilter(),"지형을 선택하세요"));
        TaskDialog.Show("CivilBIM · 2/2","시추주상도로 만든 지층 일반모델을 선택하세요. 하나 또는 여러 지층을 선택한 뒤 완료를 누르세요.");
        var elements=ui.Selection.PickObjects(ObjectType.Element,new SoilFilter(),"지층을 선택하고 완료").Select(r=>doc.GetElement(r)).DistinctBy(e=>e.Id).ToList();
        if(elements.Count==0)throw new InvalidDataException("지층을 선택하세요.");
        var results=elements.Select(e=>SoilOverlap.CalculateSingle(terrain,e)).ToList();
        using var transaction=new Transaction(doc,baseline?"현황 지형·지층 중복체적 기록":"굴착후 지형·지층 중복체적 기록");transaction.Start();OverlapParameters.Ensure(doc);
        foreach(var row in results)OverlapParameters.SetSnapshot(doc.GetElement(row.Element),row,terrain,baseline);
        OverlapParameters.Schedule(doc);if(transaction.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("중복체적 저장이 취소되었습니다.");
        SetStatus($"{(baseline?"현황":"굴착후")} 중복체적 저장 완료: {results.Sum(r=>r.Volume):N3}m³ / {results.Count}개 지층.\n"+string.Join(" / ",results.Select(r=>$"{r.Layer}: {r.Volume:N3}m³ (지층 전체의 {(r.Total>0?r.Volume/r.Total:0):P1})")));
        ui.Selection.SetElementIds(elements.Select(e=>e.Id).ToList());
    }
    private sealed class TopoFilter:ISelectionFilter {public bool AllowElement(Element e)=>e is Toposolid t&&t.HostTopoId==ElementId.InvalidElementId;public bool AllowReference(Reference r,XYZ p)=>false;}
    private sealed class SoilFilter:ISelectionFilter {public bool AllowElement(Element e)=>Storage.Info(e)?.Kind=="SiteSoil";public bool AllowReference(Reference r,XYZ p)=>false;}
}
[Transaction(TransactionMode.Manual)]
public sealed class SoilOverlapCommand:IExternalCommand
{
    public Result Execute(ExternalCommandData data,ref string message,ElementSet elements){var doc=data.Application.ActiveUIDocument?.Document;if(doc==null)return Result.Cancelled;ModelessHost.Open(data.Application,"지층중복물량",()=>new SoilOverlapWindow(doc));return Result.Succeeded;}
}
