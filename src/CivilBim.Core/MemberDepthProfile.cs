namespace CivilBim.Core;

public sealed record ProfileMember(double Station,double Top,double Bottom);
public sealed class MemberDepthProfile
{
    public List<ProfileMember> Members {get;}
    public double Length=>Members[^1].Station;
    public MemberDepthProfile(IEnumerable<PlanSegment> segments,Point3 horizontal,Point3 vertical,Point3 datum,double datumEl,bool reverse=false)
    {
        static double Dot(Point3 a,Point3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
        double Along(Point3 p)=>Dot(p,horizontal)*(reverse?-1:1);
        double Height(Point3 p)=>datumEl+Dot(new(p.X-datum.X,p.Y-datum.Y,p.Z-datum.Z),vertical);
        var lines=segments.Where(s=>Math.Abs(Along(s.A)-Along(s.B))<.005&&Math.Abs(Height(s.A)-Height(s.B))>.3).ToList();
        if(lines.Count<2)throw new InvalidDataException("선택 레이어에 같은 방향의 수직 말뚝선이 두 개 이상 필요합니다. 도면 모드/레이어를 확인하세요.");
        var origin=lines.Min(s=>(Along(s.A)+Along(s.B))/2);
        Members=lines.Select(s=>new ProfileMember((Along(s.A)+Along(s.B))/2-origin,Math.Max(Height(s.A),Height(s.B)),Math.Min(Height(s.A),Height(s.B)))).GroupBy(p=>Math.Round(p.Station,3)).Select(g=>
        {
            if(g.Max(p=>p.Top)-g.Min(p=>p.Top)>.02||g.Max(p=>p.Bottom)-g.Min(p=>p.Bottom)>.02)throw new InvalidDataException("같은 전개 위치에 서로 다른 말뚝 깊이가 겹칩니다. 전개도 범위/레이어를 좁히세요.");return g.First();
        }).OrderBy(p=>p.Station).ToList();
        if(Length<.01)throw new InvalidDataException("전개도 수평 범위가 없습니다.");
    }
    public (double Top,double Bottom) At(double station)
    {
        if(station<-.02||station>Length+.02)throw new InvalidDataException($"평면 부재 전개거리 {station:F3}m가 읽은 말뚝 구간0~{Length:F3}m 밖입니다. 경로 시작/방향 또는 구간 시작거리를 확인하세요.");
        station=Math.Clamp(station,0,Length);var right=Members.FindIndex(p=>p.Station>=station);if(right<=0)return(Members[0].Top,Members[0].Bottom);
        var a=Members[right-1];var b=Members[right];var t=(station-a.Station)/(b.Station-a.Station);return(a.Top+t*(b.Top-a.Top),a.Bottom+t*(b.Bottom-a.Bottom));
    }
}
