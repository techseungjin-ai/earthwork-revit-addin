namespace CivilBim.Core;

public static class Geology
{
    public static double SoftRockTop(Point3 position,BoreholeSet data,string crs,double radius)
    {
        data.Validate(crs);Guard.Finite(radius);if(radius<=0)throw new InvalidDataException("영향반경은 양수여야 합니다.");
        var nearby=data.Boreholes.Select(b=>(B:b,D:new Point3(b.Easting,b.Northing).DistanceXY(position))).Where(x=>x.D<=radius).OrderBy(x=>x.D).ThenBy(x=>x.B.Id,StringComparer.Ordinal).FirstOrDefault();
        if(nearby.B==null)throw new InvalidDataException("반경 내 시추공이 없습니다.");
        var index=nearby.B.Layers.FindIndex(l=>l.Name.Contains("연암"));if(index<0)throw new InvalidDataException($"{nearby.B.Id}: 연암층 경계를 찾지 못했습니다.");
        return nearby.B.GroundElevation-(index==0?0:nearby.B.Layers[index-1].BottomDepth);
    }
    // 표고 기준으로 경계를 보간한다. 지표면 심도를 평균하지 않는다.
    public static List<PileSegment> Split(string pileId, Point3 surveyPosition, double top, double bottom,
        double diameter, BoreholeSet data, string crs, GeologyMethod method, double radius)
    {
        Guard.Finite(surveyPosition.X,surveyPosition.Y,top,bottom,diameter,radius);
        if(top<=bottom || diameter<=0 || radius<=0) throw new InvalidDataException("말뚝 상하단/직경/영향반경 확인 필요.");
        data.Validate(crs);
        var nearby=data.Boreholes.Select(b=>(B:b,D:new Point3(b.Easting,b.Northing).DistanceXY(surveyPosition)))
            .Where(x=>x.D<=radius).OrderBy(x=>x.D).ThenBy(x=>x.B.Id,StringComparer.Ordinal).ToList();
        if(nearby.Count==0) throw new InvalidDataException($"{pileId}: 영향반경 내 시추공이 없습니다.");
        if(method==GeologyMethod.Nearest || nearby[0].D<0.001) nearby=[nearby[0]];
        else
        {
            if(nearby.Count<3) throw new InvalidDataException("보간에는 반경 내 시추공 3개 이상 필요.");
            var names=nearby[0].B.Layers.Select(x=>x.Name).ToArray();
            if(nearby.Any(x=>!x.B.Layers.Select(l=>l.Name).SequenceEqual(names)))
                throw new InvalidDataException("보간할 공들의 지층 이름/순서가 다릅니다. 지층 대응을 먼저 정리하세요.");
            if(!InsideHull(surveyPosition,nearby.Select(x=>new Point3(x.B.Easting,x.B.Northing)).ToList()))
                throw new InvalidDataException("시추공 경계 밖에서는 보간하지 않습니다. 최근접 방식 또는 추가 시추자료를 사용하세요.");
        }
        var weights=nearby.Select(x=>1/Math.Max(x.D*x.D,1e-6)).ToArray(); var sum=weights.Sum();
        double Average(Func<Borehole,double> f)=>nearby.Select((x,i)=>f(x.B)*weights[i]).Sum()/sum;
        var surface=Average(b=>b.GroundElevation);
        var boundaries=nearby[0].B.Layers.Select((_,i)=>Average(b=>b.GroundElevation-b.Layers[i].BottomDepth)).ToArray();
        if(top>surface+0.001 || bottom<boundaries[^1]-0.001)
            throw new InvalidDataException($"{pileId}: 말뚝이 확인된 지층 범위 밖에 있습니다 (EL {boundaries[^1]:F2}~{surface:F2}).");
        var output=new List<PileSegment>(); var upper=surface;
        for(var i=0;i<boundaries.Length;i++)
        {
            var t=Math.Min(top,upper); var b=Math.Max(bottom,boundaries[i]);
            if(t>b+1e-8) output.Add(new(pileId,nearby[0].B.Layers[i].Name,t,b,diameter,string.Join(";",nearby.Select(x=>x.B.Id))));
            upper=boundaries[i];
        }
        if(Math.Abs(output.Sum(x=>x.Length)-(top-bottom))>1e-6) throw new InvalidDataException("분할 길이 보존 검증 실패.");
        return output;
    }
    private static bool InsideHull(Point3 p,List<Point3> points)
    {
        // 단조 체인 convex hull. 외삽 방지용이며 삼각분할 지층 모델은 아니다.
        static double Cross(Point3 a,Point3 b,Point3 c)=>(b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
        var sorted=points.Distinct().OrderBy(x=>x.X).ThenBy(x=>x.Y).ToList();
        var hull=new List<Point3>();
        foreach(var x in sorted) { while(hull.Count>=2 && Cross(hull[^2],hull[^1],x)<=0) hull.RemoveAt(hull.Count-1); hull.Add(x); }
        var lower=hull.Count;
        foreach(var x in sorted.AsEnumerable().Reverse().Skip(1)) { while(hull.Count>lower && Cross(hull[^2],hull[^1],x)<=0) hull.RemoveAt(hull.Count-1); hull.Add(x); }
        if(hull.Count<4) return false;
        for(var i=0;i<hull.Count-1;i++) if(Cross(hull[i],hull[i+1],p)<-1e-6) return false;
        return true;
    }
}
