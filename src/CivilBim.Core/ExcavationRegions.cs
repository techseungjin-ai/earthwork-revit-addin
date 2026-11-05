using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Polygonize;
using NetTopologySuite.Operation.Union;

namespace CivilBim.Core;

public static class ExcavationRegions
{
    // All inputs are local model metres. Only actual intersections are noded;
    // gaps are not silently bridged.
    public static List<Polygon> Split(IEnumerable<IReadOnlyList<Point3>> paths,Polygon boundary,double gapTolerance=0)
    {
        if(!double.IsFinite(gapTolerance)||gapTolerance<0||gapTolerance>.1)throw new InvalidDataException("연결 허용 오차는 0~100mm로 입력하세요.");
        if(!boundary.IsValid||boundary.Area<.01)throw new InvalidDataException("외곽 경계가 올바르지 않습니다.");
        var segments=new List<(Coordinate A,Coordinate B)>();
        foreach(var path in paths)
        {
            for(var i=1;i<path.Count;i++)
            {
                var a=path[i-1];var b=path[i];if(a.DistanceXY(b)<.001)continue;
                segments.Add((new(a.X,a.Y),new(b.X,b.Y)));
            }
        }
        var boundarySegments=boundary.ExteriorRing.Coordinates.Zip(boundary.ExteriorRing.Coordinates.Skip(1),(a,b)=>(A:a,B:b)).ToList();
        Coordinate Snap(Coordinate p,int own)
        {
            if(gapTolerance==0)return p.Copy();double best=double.PositiveInfinity;Coordinate? target=null;bool endpoint=false;
            foreach(var (s,index) in segments.Select((s,i)=>(s,i)).Concat(boundarySegments.Select(s=>(s,-1))))
            {
                if(index==own)continue;var nearest=new LineSegment(s.A,s.B).ClosestPoint(p);var distance=nearest.Distance(p);
                if(distance<1e-9)return p.Copy();if(distance>gapTolerance||distance>=best)continue;
                best=distance;target=nearest;endpoint=index>=0&&(nearest.Distance(s.A)<1e-9||nearest.Distance(s.B)<1e-9);
            }
            if(target==null)return p.Copy();return endpoint?new Coordinate((p.X+target.X)/2,(p.Y+target.Y)/2):target.Copy();
        }
        var lines=new List<Geometry>{boundary.Boundary};for(var i=0;i<segments.Count;i++)
        {
            var a=Snap(segments[i].A,i);var b=Snap(segments[i].B,i);if(a.Distance(b)<1e-9)continue;
            var inside=Polygons.Factory.CreateLineString(new[]{a,b}).Intersection(boundary);if(!inside.IsEmpty)lines.Add(inside);
        }
        var all=Polygons.Factory.BuildGeometry(lines);
        var network=UnaryUnionOp.Union(all);var polygonizer=new Polygonizer();polygonizer.Add(network);
        var cells=polygonizer.GetPolygons().Cast<Polygon>().Where(p=>p.IsValid&&p.Area>.01&&boundary.Covers(p.InteriorPoint)).OrderByDescending(p=>p.Area).ToList();
        if(cells.Count==0)throw new InvalidDataException("닫힌 구역을 찾지 못했습니다. 경계선의 틈과 교차 여부를 확인하세요.");
        var area=cells.Sum(p=>p.Area);if(Math.Abs(area-boundary.Area)>Math.Max(.01,boundary.Area*.00001))throw new InvalidDataException("선이 끊기거나 겹쳐 구역의 면적을 확인할 수 없습니다. 외곽선과 사면 경계의 연결을 확인하세요.");
        return cells;
    }
}
