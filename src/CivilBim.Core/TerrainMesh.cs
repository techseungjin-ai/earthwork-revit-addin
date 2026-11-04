using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

namespace CivilBim.Core;

public sealed record TerrainTriangle(Point3 A,Point3 B,Point3 C)
{
    public double Area=>Math.Abs((B.X-A.X)*(C.Y-A.Y)-(B.Y-A.Y)*(C.X-A.X))/2;
    public double? Elevation(double x,double y)
    {
        var d=(B.Y-C.Y)*(A.X-C.X)+(C.X-B.X)*(A.Y-C.Y);if(Math.Abs(d)<1e-12)return null;
        var u=((B.Y-C.Y)*(x-C.X)+(C.X-B.X)*(y-C.Y))/d;
        var v=((C.Y-A.Y)*(x-C.X)+(A.X-C.X)*(y-C.Y))/d;var w=1-u-v;
        if(u< -1e-7 || v< -1e-7 || w< -1e-7)return null;return u*A.Z+v*B.Z+w*C.Z;
    }
    public Polygon Polygon=>Polygons.Factory.CreatePolygon(new[]{new Coordinate(A.X,A.Y),new Coordinate(B.X,B.Y),new Coordinate(C.X,C.Y),new Coordinate(A.X,A.Y)});
}
public sealed class TerrainMesh
{
    private readonly STRtree<TerrainTriangle> index=new();
    public IReadOnlyList<TerrainTriangle> Triangles {get;}
    public TerrainMesh(IEnumerable<TerrainTriangle> triangles)
    {
        Triangles=triangles.Where(t=>t.Area>1e-8).ToList();foreach(var t in Triangles)index.Insert(t.Polygon.EnvelopeInternal,t);index.Build();
    }
    public double? Elevation(double x,double y)
    {foreach(var t in index.Query(new Envelope(x,x,y,y))){var z=t.Elevation(x,y);if(z.HasValue)return z;}return null;}
    public double? NearElevation(double x,double y,double tolerance)
    {
        var exact=Elevation(x,y);if(exact.HasValue)return exact;
        var point=Polygons.Factory.CreatePoint(new Coordinate(x,y));var envelope=new Envelope(x,x,y,y);envelope.ExpandBy(tolerance);
        foreach(var triangle in index.Query(envelope).OrderBy(t=>t.Polygon.Distance(point)))
        {
            if(triangle.Polygon.Distance(point)>tolerance)break;
            var closest=NetTopologySuite.Operation.Distance.DistanceOp.NearestPoints(triangle.Polygon,point)[0];
            var z=triangle.Elevation(closest.X,closest.Y);if(z.HasValue)return z;
        }
        return null;
    }
    public IEnumerable<TerrainTriangle> In(Envelope bounds)=>index.Query(bounds);
}
