using System.Text.RegularExpressions;

namespace CivilBim.Core;

public sealed record DrawingText(string Layer,Point3 Position,string Text);
public sealed record DepthCandidate(string Label,double? TopElevation,double? BottomElevation,double? Length,string Evidence)
{
    public double? ObservedElevation {get;init;}
    public double? Diameter {get;init;}
    public bool BottomAtSoftRockTop {get;init;}
}
public static class DrawingDepth
{
    public static List<DrawingText> ReadExtract(string path)=>File.ReadLines(path).Where(l=>l.StartsWith("TEXT|",StringComparison.Ordinal)).Select(l=>
    {
        var c=l.Split('|',5);if(c.Length!=5)throw new InvalidDataException("도면 추출 행 오류.");
        var text=Regex.Replace(c[4],@"\\P"," ");text=Regex.Replace(text,@"\\[A-Za-z][^;]*;","").Replace("{","").Replace("}","");
        return new DrawingText(c[1],new(Guard.Number(c[2]),Guard.Number(c[3])),text);
    }).ToList();
    public static List<DepthCandidate> Recognize(IEnumerable<DrawingText> source)
    {
        var result=new List<DepthCandidate>();
        foreach(var t in source)
        {
            var id=Regex.Match(t.Text,@"(?:C[.\s]*I[.\s]*P|PILE|말뚝)\s*[-#]?\s*\d+",RegexOptions.IgnoreCase).Value;
            var elevations=Regex.Matches(t.Text,@"(?:E\s*[.]*\s*L\s*[.]*|표고)\s*[:=]?\s*([+-]?\d+(?:\.\d+)?)",RegexOptions.IgnoreCase)
                .Select(m=>Guard.Number(m.Groups[1].Value)).ToList();
            var lengthMatch=Regex.Match(t.Text,@"(?:\bL|천공장|말뚝길이|근입장)\s*[:=]\s*(\d+(?:\.\d+)?)\s*(mm|m)?",RegexOptions.IgnoreCase);
            double? length=lengthMatch.Success?Guard.Number(lengthMatch.Groups[1].Value):null;
            if(length.HasValue && lengthMatch.Groups[2].Value.Equals("mm",StringComparison.OrdinalIgnoreCase))length/=1000;
            // 단위 없는 큰 길이는 도면 단위 추정에 의존하므로 확정하지 않는다.
            if(length>200 && string.IsNullOrEmpty(lengthMatch.Groups[2].Value))length=null;
            var softRock=t.Text.Contains("연암상단까지");
            var diameterMatch=Regex.Match(t.Text,@"[ØøΦφ∅]\s*(\d+(?:\.\d+)?)");
            if(elevations.Count==0 && length==null && !softRock)continue;
            var label=string.IsNullOrWhiteSpace(id)?$"{t.Layer} @ {t.Position.X:F1},{t.Position.Y:F1}":id;
            double? top=null,bottom=null;
            if(elevations.Count==2){top=elevations.Max();bottom=elevations.Min();}
            else if(elevations.Count==1)
            {
                if(Regex.IsMatch(t.Text,"상단|두부|TOP|T\\.O",RegexOptions.IgnoreCase))top=elevations[0];
                else if(Regex.IsMatch(t.Text,"하단|선단|BOTTOM|B\\.O",RegexOptions.IgnoreCase))bottom=elevations[0];
            }
            if(top.HasValue && bottom==null && length.HasValue)bottom=top-length;
            if(bottom.HasValue && top==null && length.HasValue)top=bottom+length;
            result.Add(new(label,top,bottom,length,t.Text) {ObservedElevation=elevations.Count==1?elevations[0]:null,BottomAtSoftRockTop=softRock,Diameter=diameterMatch.Success?Guard.Number(diameterMatch.Groups[1].Value)/1000:null});
        }
        return result.Distinct().ToList();
    }
}
