using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Polygonize;
using NetTopologySuite.Operation.Union;

namespace CivilBim.Core;

public static class ExcavationRegions
{
    // All inputs are local model metres. Only actual intersections are noded;
    // gaps are not silently bridged.
    public static List<Polygon> Split(IEnumerable<IReadOnlyList<Point3>> paths,Polygon boundary)
    {
        if(!boundary.IsValid||boundary.Area<.01)throw new InvalidDataException("외곽 경계가 올바르지 않습니다.");
        var lines=new List<Geometry>{boundary.Boundary};
        foreach(var path in paths)
        {
            for(var i=1;i<path.Count;i++)
            {
                var a=path[i-1];var b=path[i];if(a.DistanceXY(b)<.001)continue;
                var segment=Polygons.Factory.CreateLineString(new[]{new Coordinate(a.X,a.Y),new Coordinate(b.X,b.Y)});
                var inside=segment.Intersection(boundary);if(!inside.IsEmpty)lines.Add(inside);
            }
        }
        var network=UnaryUnionOp.Union(lines);var polygonizer=new Polygonizer();polygonizer.Add(network);
        var cells=polygonizer.GetPolygons().Cast<Polygon>().Where(p=>p.IsValid&&p.Area>.01&&boundary.Covers(p.InteriorPoint)).OrderByDescending(p=>p.Area).ToList();
        if(cells.Count==0)throw new InvalidDataException("닫힌 구역을 찾지 못했습니다. 경계선의 틈과 교차 여부를 확인하세요.");
        var area=cells.Sum(p=>p.Area);if(Math.Abs(area-boundary.Area)>Math.Max(.01,boundary.Area*.00001))throw new InvalidDataException("선이 끊기거나 겹쳐 구역의 면적을 확인할 수 없습니다. 외곽선과 사면 경계의 연결을 확인하세요.");
        return cells;
    }
}
