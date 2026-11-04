using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Precision;
using NetTopologySuite.Simplify;

namespace CivilBim.Core;
public static class RoadBoundary
{
    public static Geometry Merge(IEnumerable<Geometry> footprints,double minimumSegment)
    {
        var original=UnaryUnionOp.Union(footprints.Select(GeometryFixer.Fix).ToList());
        var snapped=GeometryPrecisionReducer.Reduce(original,new PrecisionModel(1/(minimumSegment*2)));
        var pieces=new List<Geometry>();
        foreach(var polygon in Polygons.Enumerate(GeometryFixer.Fix(snapped)))
        {
            var shell=Polygons.Factory.CreatePolygon(polygon.ExteriorRing.Coordinates);var holes=new List<Geometry>();
            for(var i=0;i<polygon.NumInteriorRings;i++)
            {
                var ring=polygon.GetInteriorRingN(i);var hole=(Geometry)Polygons.Factory.CreatePolygon(ring.Coordinates);
                var touching=ring.Distance(polygon.ExteriorRing)<=minimumSegment*2||Enumerable.Range(0,polygon.NumInteriorRings).Any(j=>j!=i&&ring.Distance(polygon.GetInteriorRingN(j))<=minimumSegment*2);
                if(touching)hole=hole.Buffer(minimumSegment*2);holes.Add(hole);
            }
            pieces.Add(holes.Count==0?shell:shell.Difference(UnaryUnionOp.Union(holes)));
        }
        var clean=GeometryFixer.Fix(TopologyPreservingSimplifier.Simplify(UnaryUnionOp.Union(pieces),minimumSegment*1.5));
        if(clean.IsEmpty||!clean.IsValid)throw new InvalidDataException("합친 도로의 폐합 경계를 정리할 수 없습니다. 원본 도로를 유지합니다.");
        if(original.SymmetricDifference(clean).Area>Math.Max(.02,original.Area*.0001))throw new InvalidDataException("도로 경계 정리 과정의 면적 변화가 커서 병합을 중단했습니다. 원본 도로를 유지합니다.");
        return clean;
    }
}
