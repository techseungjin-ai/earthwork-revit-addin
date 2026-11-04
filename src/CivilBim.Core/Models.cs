using System.Globalization;
using System.Text.Json;

namespace CivilBim.Core;

public readonly record struct Point3(double X, double Y, double Z = 0)
{
    public double DistanceXY(Point3 b) => Math.Sqrt((X-b.X)*(X-b.X)+(Y-b.Y)*(Y-b.Y));
}

public sealed record SiteFrame(string Crs, double Easting, double Northing, double Elevation = 0, double RotationDegrees = 0)
{
    public Point3 ToLocal(Point3 p)
    {
        Validate();
        var a = RotationDegrees * Math.PI / 180;
        var x = p.X - Easting; var y = p.Y - Northing;
        return new(x*Math.Cos(a)-y*Math.Sin(a), x*Math.Sin(a)+y*Math.Cos(a), p.Z-Elevation);
    }
    public Point3 ToSurvey(Point3 p)
    {
        Validate();
        var a = RotationDegrees * Math.PI / 180;
        return new(p.X*Math.Cos(a)+p.Y*Math.Sin(a)+Easting, -p.X*Math.Sin(a)+p.Y*Math.Cos(a)+Northing, p.Z+Elevation);
    }
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Crs)) throw new InvalidDataException("좌표계 식별자를 입력하세요.");
        Guard.Finite(Easting, Northing, Elevation, RotationDegrees);
    }
    public SiteFrame RotateAbout(Point3 pivot,double degrees)
    {
        Validate();Guard.Finite(pivot.X,pivot.Y,degrees);
        var survey=ToSurvey(pivot);var rotation=RotationDegrees+degrees;
        var offset=new SiteFrame(Crs,0,0,Elevation,rotation).ToSurvey(pivot);
        return new(Crs,survey.X-offset.X,survey.Y-offset.Y,Elevation,rotation);
    }
}

public static class Guard
{
    public static void Finite(params double[] v) { if (v.Any(x=>!double.IsFinite(x))) throw new InvalidDataException("유한한 수치가 필요합니다."); }
    public static double Number(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
}

public sealed record Stratum(string Name, double BottomDepth);
public sealed record Borehole(string Id, double Easting, double Northing, double GroundElevation, List<Stratum> Layers)
{
    public void Validate()
    {
        Guard.Finite(Easting, Northing, GroundElevation);
        if (string.IsNullOrWhiteSpace(Id) || Layers.Count == 0) throw new InvalidDataException("공번과 지층이 필요합니다.");
        var previous=0.0;
        foreach (var layer in Layers)
        {
            Guard.Finite(layer.BottomDepth);
            if (string.IsNullOrWhiteSpace(layer.Name) || layer.Name.Contains('\ufffd') || layer.Name.Contains("검토필요") || layer.BottomDepth <= previous)
                throw new InvalidDataException($"{Id}: 지층 이름/하단 심도 확인 필요 ({layer.Name}).");
            previous=layer.BottomDepth;
        }
    }
}
public sealed class BoreholeSet
{
    public string Crs { get; set; } = "";
    public bool Reviewed { get; set; }
    public string Source { get; set; } = "";
    public List<string> ReviewNotes { get; set; } = [];
    public List<Borehole> Boreholes { get; set; } = [];
    public static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented=true, PropertyNameCaseInsensitive=true, Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public void Validate(string crs)
    {
        if (!Reviewed) throw new InvalidDataException("시추자료 검토 완료를 확인하세요.");
        if (!string.Equals(Crs.Trim(),crs.Trim(),StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHP/프로젝트와 시추자료의 좌표계 식별자가 다릅니다.");
        if (Boreholes.Count==0 || Boreholes.Select(x=>x.Id).Distinct().Count()!=Boreholes.Count) throw new InvalidDataException("시추공 누락/공번 중복.");
        foreach(var b in Boreholes) b.Validate();
        for(var i=0;i<Boreholes.Count;i++) for(var j=i+1;j<Boreholes.Count;j++)
            if(new Point3(Boreholes[i].Easting,Boreholes[i].Northing).DistanceXY(new(Boreholes[j].Easting,Boreholes[j].Northing))<0.001)
                throw new InvalidDataException("같은 위치의 중복 시추공을 정리하세요.");
    }
}
public enum GeologyMethod { Nearest, Idw }
public sealed record PileSegment(string PileId, string Layer, double TopElevation, double BottomElevation, double Diameter, string BoreholeIds)
{
    public double Length => TopElevation-BottomElevation;
    public double Volume => Math.PI*Diameter*Diameter/4*Length;
}
