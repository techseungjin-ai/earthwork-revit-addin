using System.Globalization;
using System.Text.RegularExpressions;

namespace CivilBim.Core;

// Conservative WKT1 comparison: ignore presentation, preserve projection definitions.
public static class ProjectionComparison
{
    public static bool Equivalent(string a,string b)
    {
        if(string.IsNullOrWhiteSpace(a)||string.IsNullOrWhiteSpace(b))return false;
        return Signature(a)==Signature(b);
    }
    private static string Signature(string wkt)
    {
        // Display names of coordinate systems do not define their mathematics.
        var text=Regex.Replace(wkt,@"\b(PROJCS|GEOGCS)\s*\[\s*""[^""]*""",m=>m.Groups[1].Value+"[");
        var parameters=new List<string>();
        text=Regex.Replace(text,@"PARAMETER\s*\[\s*""[^""]+""\s*,\s*[-+0-9.eE]+\s*\]\s*,?",m=>{parameters.Add(Tokens(m.Value.TrimEnd().TrimEnd(',')));return "";},RegexOptions.IgnoreCase);
        parameters.Sort(StringComparer.Ordinal);
        return Tokens(text)+"|"+string.Join("|",parameters);
    }
    private static string Tokens(string text)
    {
        return string.Concat(Regex.Matches(text,@"""[^""]*""|[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?|[A-Za-z_]+|[\[\],]").Select(m=>
        {
            var token=m.Value;
            if(token.StartsWith('"'))return "\""+Regex.Replace(token.Trim('"'),@"[\s_]","").ToUpperInvariant()+"\"";
            if(double.TryParse(token,NumberStyles.Float,CultureInfo.InvariantCulture,out var n))return n.ToString("G12",CultureInfo.InvariantCulture);
            return token.ToUpperInvariant();
        }));
    }
    public static void RequireSame(string baseline,string candidate,string file)
    {
        if(!Equivalent(baseline,candidate))throw new InvalidDataException($"SHP 좌표계 정의가 다르거나 PRJ가 없습니다: {file}\n기준 {SiteDataset.ProjectionId(baseline)} / 대상 {SiteDataset.ProjectionId(candidate)}. 같은 투영 좌표계로 변환한 SHP를 사용하세요.");
    }
}
