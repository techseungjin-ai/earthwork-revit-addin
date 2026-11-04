namespace CivilBim.Core;

// A vertically placed, full-size drawing: horizontal distance is station, actual Z is elevation.
public sealed class VerticalProfile
{
    private readonly List<PlanSegment> upper,lower;
    private readonly Point3 axis;
    private readonly double origin;
    public double Length {get;}
    public VerticalProfile(IEnumerable<PlanSegment> top,IEnumerable<PlanSegment> bottom,bool reverse=false)
    {
        upper=top.ToList();lower=bottom.ToList();var all=upper.Concat(lower).ToList();
        if(upper.Count==0||lower.Count==0)throw new InvalidDataException("전개도 상단·하단 선을 각각 선택하세요.");
        var longest=all.MaxBy(s=>s.A.DistanceXY(s.B))!;var length=longest.A.DistanceXY(longest.B);
        if(length<.01)throw new InvalidDataException("전개도에 수평 거리 방향이 없습니다. 링크를 수직으로 세워 Z 높이를 맞추세요.");
        var x=(longest.B.X-longest.A.X)/length;var y=(longest.B.Y-longest.A.Y)/length;
        if((Math.Abs(x)>=Math.Abs(y)?x:y)<0){x=-x;y=-y;}if(reverse){x=-x;y=-y;}axis=new(x,y);
        var points=all.SelectMany(s=>new[]{s.A,s.B}).ToList();
        var normals=points.Select(p=>-p.X*axis.Y+p.Y*axis.X).ToList();
        if(normals.Max()-normals.Min()>.02)throw new InvalidDataException("상·하단 선이 같은 수직 전개면에 있지 않습니다. 해당 전개도 선만 선택하세요.");
        origin=points.Min(Project);Length=points.Max(Project)-origin;
        if(points.Max(p=>p.Z)-points.Min(p=>p.Z)<.001)throw new InvalidDataException("전개도 상·하단의 실제 Z가 같습니다. XY에 눕힌 CAD를 수직으로 세우고 높이를 맞추세요.");
    }
    private double Project(Point3 p)=>p.X*axis.X+p.Y*axis.Y;
    public (double Top,double Bottom) At(double station,double elevationOrigin=0)
    {
        double Read(IEnumerable<PlanSegment> lines)=>DevelopmentMapping.Elevation(station,lines.Select(s=>new PlanSegment(new(Project(s.A),s.A.Z),new(Project(s.B),s.B.Z))),origin,1,0,elevationOrigin,1);
        var top=Read(upper);var bottom=Read(lower);
        if(top<=bottom)throw new InvalidDataException($"전개거리 {station:F3}m의 상·하단이 역전되었습니다.");return(top,bottom);
    }
    public void CheckLength(double planLength)
    {
        if(Math.Abs(Length-planLength)>Math.Max(.02,planLength*.005))throw new InvalidDataException($"평면 경로 {planLength:F3}m / 전개도 {Length:F3}m가 다릅니다. 같은 구간의 1:1 전개도 상·하단 선을 선택하세요. 자동 확대/축소는 하지 않습니다.");
    }
}
