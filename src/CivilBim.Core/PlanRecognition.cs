namespace CivilBim.Core;

public sealed record PlanSegment(Point3 A,Point3 B)
{
    public Point3 Mid=>new((A.X+B.X)/2,(A.Y+B.Y)/2,(A.Z+B.Z)/2);
    public double Length=>A.DistanceXY(B);
    public double Angle=>Math.Atan2(B.Y-A.Y,B.X-A.X);
}
public sealed record PileSymbol(Point3 Center,double Angle,string Source);
public static class PlanRecognition
{
    public static List<PileSymbol> HPiles(IEnumerable<PlanSegment> source,double connectionTolerance=.003)
    {
        var segments=source.Where(s=>s.Length>.005 && s.Length<2).ToList();var seen=new bool[segments.Count];var result=new List<PileSymbol>();
        double Distance(Point3 p,PlanSegment s)
        {var dx=s.B.X-s.A.X;var dy=s.B.Y-s.A.Y;var t=Math.Clamp(((p.X-s.A.X)*dx+(p.Y-s.A.Y)*dy)/(dx*dx+dy*dy),0,1);return p.DistanceXY(new(s.A.X+t*dx,s.A.Y+t*dy));}
        bool Touch(PlanSegment a,PlanSegment b)=>Math.Min(Math.Min(Distance(a.A,b),Distance(a.B,b)),Math.Min(Distance(b.A,a),Distance(b.B,a)))<=connectionTolerance;
        for(var i=0;i<segments.Count;i++)
        {
            if(seen[i])continue;seen[i]=true;var queue=new Queue<int>();queue.Enqueue(i);var group=new List<PlanSegment>();
            while(queue.Count>0)
            {
                var n=queue.Dequeue();var current=segments[n];group.Add(current);
                for(var j=0;j<segments.Count;j++)if(!seen[j]&&current.Mid.DistanceXY(segments[j].Mid)<2&&Touch(current,segments[j])){seen[j]=true;queue.Enqueue(j);}
            }
            if(group.Count<3 || group.Count>32)continue;
            if(group.Count>3 && group.Count<8)continue;
            // 개방 H의 두 평행 플랜지와 웨브, 또는 폐합 H 윤곽의 주축을 사용한다.
            var dominant=group.OrderByDescending(s=>s.Length).First();var angle=dominant.Angle;
            var parallel=group.Where(s=>Math.Abs(Math.Sin(s.Angle-angle))<.02).ToList();var perpendicular=group.Where(s=>Math.Abs(Math.Cos(s.Angle-angle))<.02).ToList();
            if(parallel.Count<1 || perpendicular.Count<1)continue;
            if((parallel.Count==1 && perpendicular.Count>=2) || (group.Count>=8 && parallel.Min(s=>s.Length)<perpendicular.Min(s=>s.Length)*.5))angle+=Math.PI/2;
            else if(parallel.Count<2)continue;
            if(group.Count==3)
            {
                var flanges=group.Where(s=>Math.Abs(Math.Sin(s.Angle-angle))<.02).ToList();var webs=group.Where(s=>Math.Abs(Math.Cos(s.Angle-angle))<.02).ToList();
                if(flanges.Count!=2||webs.Count!=1)continue;
                var web=webs[0];var f0=flanges[0].Mid;var f1=flanges[1].Mid;
                if(Math.Min(f0.DistanceXY(web.A)+f1.DistanceXY(web.B),f0.DistanceXY(web.B)+f1.DistanceXY(web.A))>connectionTolerance*4)continue;
            }
            var points=group.SelectMany(s=>new[]{s.A,s.B}).ToList();var center=new Point3(points.Average(p=>p.X),points.Average(p=>p.Y),points.Average(p=>p.Z));
            if(points.Any(p=>p.DistanceXY(center)>1))continue;
            result.Add(new(center,Normalize(angle),"H형 선군"));
        }
        return result.DistinctBy(p=>(Math.Round(p.Center.X,4),Math.Round(p.Center.Y,4))).ToList();
    }
    public static double Normalize(double radians){var a=radians%Math.PI;return a<0?a+Math.PI:a;}
}
public static class ElevationMatching
{
    public static double At(Point3 pile,IEnumerable<PlanSegment> curves,double tolerance)
    {
        var best=double.PositiveInfinity;double? z=null;
        foreach(var c in curves)
        {
            var dx=c.B.X-c.A.X;var dy=c.B.Y-c.A.Y;var length=dx*dx+dy*dy;if(length<1e-10)continue;
            var t=((pile.X-c.A.X)*dx+(pile.Y-c.A.Y)*dy)/length;if(t< -1e-6 || t>1+1e-6)continue;t=Math.Clamp(t,0,1);
            var distance=pile.DistanceXY(new(c.A.X+t*dx,c.A.Y+t*dy));
            if(distance<best){best=distance;z=c.A.Z+t*(c.B.Z-c.A.Z);}
        }
        if(z==null || best>tolerance)throw new InvalidDataException("해당 말뚝 위치에 대응하는 상하단 3D 곡선이 없습니다.");return z.Value;
    }
    public static List<double> Tiers(IEnumerable<PlanSegment> horizontalLines,double elevationTolerance=.01)
    {
        var values=horizontalLines.Where(s=>s.Length>.1 && Math.Abs(s.A.Z-s.B.Z)<=elevationTolerance).Select(s=>(s.A.Z+s.B.Z)/2).OrderDescending().ToList();
        var result=new List<double>();foreach(var z in values)if(result.All(v=>Math.Abs(v-z)>elevationTolerance))result.Add(z);return result;
    }
}
