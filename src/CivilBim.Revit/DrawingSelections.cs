using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CivilBim.Core;

namespace CivilBim.Revit;

public static class DrawingSelections
{
    public sealed class Filter:ISelectionFilter
    {public bool AllowElement(Element e)=>e is CurveElement or ImportInstance;public bool AllowReference(Reference r,XYZ p)=>true;}
    public sealed class RevitFilter:ISelectionFilter
    {public bool AllowElement(Element e)=>e is CurveElement or ReferencePlane;public bool AllowReference(Reference r,XYZ p)=>true;}
    public static List<Point3> RevitPath(UIDocument ui,string prompt,out ElementId elementId)
    {var reference=ui.Selection.PickObject(ObjectType.Element,new RevitFilter(),prompt);elementId=reference.ElementId;return Read(ui.Document,reference);}
    public static List<PlanSegment> RevitLines(UIDocument ui,string prompt)
    {return ui.Selection.PickObjects(ObjectType.Element,new RevitFilter(),prompt).SelectMany(r=>{var p=Read(ui.Document,r);return p.Zip(p.Skip(1),(a,b)=>new PlanSegment(a,b));}).Distinct().ToList();}
    public static List<Point3> Path(UIDocument ui,string prompt)
        =>Path(ui,prompt,out _);
    public static List<Point3> Path(UIDocument ui,string prompt,out ElementId elementId)
    {
        var reference=ui.Selection.PickObject(ObjectType.PointOnElement,new Filter(),prompt);elementId=reference.ElementId;return Read(ui.Document,reference);
    }
    public static List<PlanSegment> Lines(UIDocument ui,string prompt)
    {
        var references=ui.Selection.PickObjects(ObjectType.PointOnElement,new Filter(),prompt);var result=new List<PlanSegment>();
        foreach(var reference in references){var p=Read(ui.Document,reference);result.AddRange(p.Zip(p.Skip(1),(a,b)=>new PlanSegment(a,b)));}
        return result.Distinct().ToList();
    }
    private static List<Point3> Read(Document doc,Reference reference)
    {
        var element=doc.GetElement(reference);
        if(element is ReferencePlane plane)return new[]{plane.BubbleEnd,plane.FreeEnd}.Select(ModelBuilder.Meters).ToList();
        if(element is CurveElement curve)return curve.GeometryCurve.Tessellate().Select(ModelBuilder.Meters).ToList();
        if(element is not ImportInstance cad)throw new InvalidDataException("Revit 선 또는 CAD 선/폴리선을 선택하세요.");
        var reader=CadReader.Read(doc,cad);var layer=ScreenChoices.ResolveLayer(doc,cad,reference,reader);var point=ModelBuilder.Meters(reference.GlobalPoint);
        double Distance(CadPath path)
        {
            var p=path.Points.Select(ModelBuilder.Meters).ToList();var best=double.PositiveInfinity;
            for(var i=1;i<p.Count;i++)
            {var a=p[i-1];var b=p[i];var dx=b.X-a.X;var dy=b.Y-a.Y;var dz=b.Z-a.Z;var squared=dx*dx+dy*dy+dz*dz;if(squared<1e-12)continue;var t=Math.Clamp(((point.X-a.X)*dx+(point.Y-a.Y)*dy+(point.Z-a.Z)*dz)/squared,0,1);best=Math.Min(best,Math.Sqrt(Math.Pow(point.X-a.X-t*dx,2)+Math.Pow(point.Y-a.Y-t*dy,2)+Math.Pow(point.Z-a.Z-t*dz,2)));}return best;
        }
        string Key(CadPath path){var forward=string.Join(";",path.Points.Select(p=>FormattableString.Invariant($"{p.X:F6},{p.Y:F6},{p.Z:F6}")));var reverse=string.Join(";",path.Points.AsEnumerable().Reverse().Select(p=>FormattableString.Invariant($"{p.X:F6},{p.Y:F6},{p.Z:F6}")));return string.CompareOrdinal(forward,reverse)<0?forward:reverse;}
        var candidates=reader.Paths.Where(p=>p.Layer==layer).GroupBy(Key).Select(g=>g.First()).Select(p=>(Path:p,Distance:Distance(p))).OrderBy(p=>p.Distance).ToList();
        if(candidates.Count==0||candidates[0].Distance>.02)throw new InvalidDataException("선택한 CAD 선을 찾지 못했습니다. 선을 확대해 다시 선택하세요.");
        if(candidates.Count>1&&Math.Abs(candidates[0].Distance-candidates[1].Distance)<1e-6)throw new InvalidDataException("여러 CAD 선이 겹칩니다. 꼭짓점 대신 선 중간을 선택하세요.");
        return candidates[0].Path.Points.Select(ModelBuilder.Meters).ToList();
    }
    public static double NorthAngle(IReadOnlyList<Point3> from,IReadOnlyList<Point3> to,bool reverseFrom=false,bool reverseTo=false)
    {
        double Angle(IReadOnlyList<Point3> p,bool reverse){if(p.Count<2||p[0].DistanceXY(p[^1])<.01)throw new InvalidDataException("북방향은 열린 직선으로 선택하세요.");var a=p[0];var b=p[^1];foreach(var point in p)if(Math.Abs((b.X-a.X)*(point.Y-a.Y)-(b.Y-a.Y)*(point.X-a.X))/a.DistanceXY(b)>.001)throw new InvalidDataException("북방향은 꺾이지 않은 직선이어야 합니다.");return Math.Atan2(b.Y-a.Y,b.X-a.X)+(reverse?Math.PI:0);}
        var value=(Angle(to,reverseTo)-Angle(from,reverseFrom))*180/Math.PI;return Math.IEEERemainder(value,360);
    }
}
