using NetTopologySuite.Algorithm;

namespace CivilBim.Core;

public sealed record PlanPath(List<Point3> Points,bool Closed);
public static class HorizontalRecognition
{
    public static List<PlanSegment> Centerlines(IEnumerable<PlanPath> paths)
    {
        var result=new List<PlanSegment>();
        foreach(var path in paths)
        {
            if(path.Closed && path.Points.Count>=4)
            {
                var polygon=Polygons.Factory.CreatePolygon(path.Points.Select(p=>new NetTopologySuite.Geometries.Coordinate(p.X,p.Y)).ToArray());
                if(polygon.IsValid && polygon.Area>.005)
                {
                    var rectangle=MinimumDiameter.GetMinimumRectangle(polygon);var coords=rectangle.Coordinates.Take(4).ToList();
                    if(coords.Count==4 && rectangle.Area/polygon.Area<1.05)
                    {
                        var edges=Enumerable.Range(0,4).Select(i=>new PlanSegment(new(coords[i].X,coords[i].Y,path.Points[0].Z),new(coords[(i+1)%4].X,coords[(i+1)%4].Y,path.Points[0].Z))).ToList();
                        var longEdge=edges.OrderByDescending(e=>e.Length).First();var shortEdges=edges.Where(e=>e.Length<longEdge.Length/5).ToList();
                        if(shortEdges.Count==2){result.Add(new(shortEdges[0].Mid,shortEdges[1].Mid));continue;}
                    }
                }
            }
            for(var i=1;i<path.Points.Count;i++)if(path.Points[i-1].DistanceXY(path.Points[i])>.01)result.Add(new(path.Points[i-1],path.Points[i]));
        }
        string Key(Point3 p)=>$"{Math.Round(p.X,3):R},{Math.Round(p.Y,3):R}";
        return result.DistinctBy(s=>string.CompareOrdinal(Key(s.A),Key(s.B))<=0?Key(s.A)+"|"+Key(s.B):Key(s.B)+"|"+Key(s.A)).ToList();
    }
    public static List<double> DrawingTiers(IEnumerable<PlanSegment> lines,double drawingDatum,double elevationDatum,double verticalScale,double groupTolerance=.4)
    {
        Guard.Finite(drawingDatum,elevationDatum,verticalScale,groupTolerance);if(verticalScale==0 || groupTolerance<=0)throw new InvalidDataException("전개도 높이 보정값을 확인하세요.");
        var coordinates=lines.Where(s=>s.Length>.2 && Math.Abs(s.A.Y-s.B.Y)<.005).Select(s=>(s.A.Y+s.B.Y)/2).Order().ToList();
        var groups=new List<List<double>>();foreach(var y in coordinates){if(groups.Count==0 || (y-groups[^1][^1])*Math.Abs(verticalScale)>groupTolerance)groups.Add([]);groups[^1].Add(y);}
        var tiers=groups.Select(g=>((g.Min()+g.Max())/2-drawingDatum)*verticalScale+elevationDatum).OrderDescending().ToList();
        if(tiers.Count==0 || tiers.Count>30)throw new InvalidDataException("전개도 단수가 0 또는 30단을 넘습니다. 인식 레이어/영역을 확인하세요.");return tiers;
    }
}
