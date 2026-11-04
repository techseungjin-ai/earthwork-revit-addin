namespace CivilBim.Core;

public static class DevelopmentMapping
{
    public static double Station(Point3 point,IReadOnlyList<Point3> alignment,double tolerance=.5)
    {
        if(alignment.Count<2)throw new InvalidDataException("평면 전개 경로가 필요합니다.");
        double cumulative=0,bestDistance=double.PositiveInfinity,station=0;
        for(var i=1;i<alignment.Count;i++)
        {
            var a=alignment[i-1];var b=alignment[i];var dx=b.X-a.X;var dy=b.Y-a.Y;var length=a.DistanceXY(b);if(length<1e-8)continue;
            var t=Math.Clamp(((point.X-a.X)*dx+(point.Y-a.Y)*dy)/(length*length),0,1);
            var distance=point.DistanceXY(new(a.X+t*dx,a.Y+t*dy));
            if(distance<bestDistance){bestDistance=distance;station=cumulative+t*length;}cumulative+=length;
        }
        if(bestDistance>tolerance)throw new InvalidDataException($"부재가 전개 경로에서 {bestDistance:F3}m 떨어져 있습니다. 경로/부재 선택을 확인하세요.");return station;
    }
    public static double Elevation(double station,IEnumerable<PlanSegment> drawing,double x0,double xPerMeter,double y0,double el0,double elPerY)
    {
        Guard.Finite(station,x0,xPerMeter,y0,el0,elPerY);if(Math.Abs(xPerMeter)<1e-9||Math.Abs(elPerY)<1e-9)throw new InvalidDataException("전개도 축척 보정값이 0입니다.");
        var x=x0+station*xPerMeter;var candidates=new List<double>();
        foreach(var line in drawing)
        {
            if(Math.Abs(line.A.X-line.B.X)<1e-8)continue;
            var t=(x-line.A.X)/(line.B.X-line.A.X);if(t< -1e-7||t>1+1e-7)continue;
            var y=line.A.Y+t*(line.B.Y-line.A.Y);candidates.Add(el0+(y-y0)*elPerY);
        }
        if(candidates.Count==0)throw new InvalidDataException($"전개도 거리 {station:F3}m에서 경계를 찾지 못했습니다.");
        if(candidates.Max()-candidates.Min()>.02)throw new InvalidDataException($"전개도 거리 {station:F3}m에서 경계 후보가 겹칩니다. 전개도 범위/레이어를 좁히세요.");return candidates.Average();
    }
}
