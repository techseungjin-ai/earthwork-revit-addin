using Autodesk.Revit.DB;
using CivilBim.Core;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace CivilBim.Revit;

public static class SoilValidation
{
    private static List<(double Low,double High)> Merge(IEnumerable<(double Low,double High)> values)
    {
        var result=new List<(double Low,double High)>();
        foreach(var p in values.OrderBy(p=>p.Low)){if(result.Count==0||p.Low>result[^1].High+.001)result.Add(p);else result[^1]=(result[^1].Low,Math.Max(result[^1].High,p.High));}return result;
    }
    public static string Check(Document doc,BoreholeSet data)
    {
        var elements=Storage.Generated(doc).Where(g=>g.Info.Kind=="SiteSoil").ToList();if(elements.Count==0)throw new InvalidDataException("먼저 PDF로 지층을 생성하세요.");
        var frame=elements[0].Info.Frame;if(elements.Any(e=>e.Info.Frame!=frame))throw new InvalidDataException("서로 다른 좌표기준의 지층이 있습니다.");
        var settings=Storage.Load(doc);var scope=string.IsNullOrWhiteSpace(settings.WorksiteBoundaryWkt)?SiteGeology.BoreholeScope(data,frame):new NetTopologySuite.IO.WKTReader().Read(settings.WorksiteBoundaryWkt);
        var expected=SiteGeology.BuildFromBoreholes(data,frame,scope);var report=new StringBuilder();var failures=0;
        report.AppendLine("검증 범위: 계산상 시추 최근접 구역의 누락·중첩, 지층별 실제 솔리드 체적, 시추공 위치의 실제 상·하단/연속성.");
        report.AppendLine("시추공 사이를 매끈하게 보간한 모델이 아닙니다. 중앙 공의 지표 EL이 낮으면 그 구역도 낮아져 단차가 생깁니다.\n");
        var native=elements.GroupBy(e=>e.Info.Layer).ToDictionary(g=>g.Key,g=>g.SelectMany(e=>SoilOverlap.Read(e.Element)).ToList());
        foreach(var body in expected)
        {
            var volume=native.GetValueOrDefault(body.Layer)?.Sum(ModelBuilder.Volume)??0;var pass=Math.Abs(volume-body.Volume)<=Math.Max(.001,body.Volume*1e-5);if(!pass)failures++;
            report.AppendLine($"{(pass?"통과":"불일치")} {body.Layer}: 계산 {body.Volume:N3} / 실제 {volume:N3}m³");
        }
        if(native.Keys.Except(expected.Select(b=>b.Layer)).Any()){failures++;report.AppendLine("불일치: PDF에 없는 지층 모델이 있습니다.");}
        foreach(var hole in data.Boreholes)
        {
            report.AppendLine($"\n{hole.Id}: PDF 지표 EL {hole.GroundElevation:F3}m / 최종심도 {hole.Layers[^1].BottomDepth:F3}m");
            var p=frame.ToLocal(new(hole.Easting,hole.Northing,hole.GroundElevation));
            var ray=Line.CreateBound(ModelBuilder.Xyz(p with {Z=p.Z-hole.Layers[^1].BottomDepth-1}),ModelBuilder.Xyz(p with {Z=p.Z+1}));
            double previous=0;var wanted=new Dictionary<string,List<(double Low,double High)>>();
            foreach(var layer in hole.Layers){if(!wanted.TryGetValue(layer.Name,out var list)){list=[];wanted[layer.Name]=list;}list.Add((hole.GroundElevation-layer.BottomDepth,hole.GroundElevation-previous));previous=layer.BottomDepth;}
            foreach(var (layer,intervals) in wanted)
            {
                var actual=new List<(double Low,double High)>();var inconclusive=false;
                foreach(var solid in native.GetValueOrDefault(layer)??[])
                {
                    var box=solid.GetBoundingBox();var corners=(from x in new[]{box.Min.X,box.Max.X} from y in new[]{box.Min.Y,box.Max.Y} from z in new[]{box.Min.Z,box.Max.Z} select box.Transform.OfPoint(new XYZ(x,y,z))).ToList();var point=ModelBuilder.Xyz(p);
                    if(point.X<corners.Min(c=>c.X)-1e-7||point.X>corners.Max(c=>c.X)+1e-7||point.Y<corners.Min(c=>c.Y)-1e-7||point.Y>corners.Max(c=>c.Y)+1e-7)continue;
                    try
                    {
                        using var intersection=solid.IntersectWithCurve(ray,new SolidCurveIntersectionOptions());
                        for(var i=0;i<intersection.SegmentCount;i++){var c=intersection.GetCurveSegment(i);var a=c.GetEndPoint(0).Z*.3048+frame.Elevation;var b=c.GetEndPoint(1).Z*.3048+frame.Elevation;actual.Add((Math.Min(a,b),Math.Max(a,b)));}
                    }
                    catch {inconclusive=true;}
                }
                var aMerged=Merge(actual);var eMerged=Merge(intervals);var pass=!inconclusive&&aMerged.Count==eMerged.Count&&aMerged.Zip(eMerged).All(pair=>Math.Abs(pair.First.Low-pair.Second.Low)<=.002&&Math.Abs(pair.First.High-pair.Second.High)<=.002);if(!pass)failures++;
                string Text(IEnumerable<(double Low,double High)> spans)=>string.Join(" / ",spans.Select(s=>$"EL {s.High:F3}~{s.Low:F3}"));
                report.AppendLine($"  {(pass?"통과":inconclusive?"판정불가":"불일치")} {layer}: PDF {Text(eMerged)} / 모델 {(aMerged.Count==0?"교차 없음":Text(aMerged))}");
            }
        }
        report.Insert(0,(failures==0?"검증 통과":"확인 필요 "+failures+"항목")+"\n\n");
        report.AppendLine("\n이 검증은 입력 PDF와 생성 모델의 대응 확인입니다. 원문 추출값의 정확성이나 시추공 사이의 실제 지질을 확정하지 않습니다. 경계면에 측선이 놓여 판정불가이면 해당 공을 지나는 Revit 단면으로 확인하세요.");return report.ToString();
    }
}
public sealed class SoilValidationWindow:ModelessWindow
{
    private readonly TextBox report=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(18)};
    public SoilValidationWindow(){Title="시추 지층 생성 결과 검증";Width=900;Height=680;Content=report;}
    public void Update(string text)=>report.Text=text;
}
