using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Operation.OverlayNG;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Triangulate.Polygon;

namespace CivilBim.Core;

public sealed record SoilWedge(Point3 A,Point3 B,Point3 C,double Bottom)
{
    public double Area=>Math.Abs((B.X-A.X)*(C.Y-A.Y)-(B.Y-A.Y)*(C.X-A.X))/2;
    public double Volume=>Area*((A.Z+B.Z+C.Z)/3-Bottom);
    public Polygon Footprint=>Polygons.Factory.CreatePolygon(new[]{new Coordinate(A.X,A.Y),new Coordinate(B.X,B.Y),new Coordinate(C.X,C.Y),new Coordinate(A.X,A.Y)});
}
public sealed class SoilBody
{
    public string Layer {get;init;}="";
    public HashSet<string> Boreholes {get;}=[];
    public List<SoilWedge> Wedges {get;}=[];
    public List<SoilWedge> Excavation {get;}=[];
    public double Volume=>Wedges.Sum(w=>w.Volume);
    public double ExcavatedVolume=>Excavation.Sum(w=>w.Volume);
    public double ExcavatedArea=>Excavation.Count==0?0:OverlayNGRobust.Union(Excavation.Select(w=>(Geometry)w.Footprint).ToList()).Area;
}
public static class SiteGeology
{
    public static SiteFrame BoreholeFrame(BoreholeSet data,SiteFrame? project=null,SiteFrame? saved=null)
    {
        data.Validate(data.Crs);
        foreach(var frame in new[]{saved,project})if(frame!=null&&!string.IsNullOrWhiteSpace(frame.Crs)&&string.Equals(frame.Crs,data.Crs,StringComparison.OrdinalIgnoreCase)){frame.Validate();return frame;}
        return new(data.Crs,data.Boreholes.Average(b=>b.Easting),data.Boreholes.Average(b=>b.Northing));
    }
    public static List<SoilBody> BuildFromBoreholes(BoreholeSet data,SiteFrame frame,Geometry? scope=null)
    {
        data.Validate(frame.Crs);frame.Validate();var site=scope??BoreholeScope(data,frame);
        if(!site.IsValid||site.IsEmpty||site.Area<=.01)throw new InvalidDataException("시추공 생성 범위가 유효하지 않습니다.");
        var cells=Cells(site,data,frame,1,false);var coverage=OverlayNGRobust.Union(cells.Select(c=>c.Cell));
        if(OverlayNGRobust.Overlay(site,coverage,SpatialFunction.SymDifference).Area>Math.Max(1e-6,site.Area*1e-8)||Math.Abs(cells.Sum(c=>c.Cell.Area)-coverage.Area)>Math.Max(1e-6,site.Area*1e-8))throw new InvalidDataException("시추공 영향구역에 누락 또는 중첩이 있습니다.");
        var bodies=new Dictionary<string,SoilBody>();double expectedVolume=0;
        foreach(var (hole,cell) in cells)
        {
            expectedVolume+=cell.Area*hole.Layers[^1].BottomDepth;
            var triangles=SoilPolygons(cell).SelectMany(p=>SoilPolygons(PolygonTriangulator.Triangulate(p))).ToList();
            double previous=0;
            foreach(var layer in hole.Layers)
            {
                if(!bodies.TryGetValue(layer.Name,out var body)){body=new(){Layer=layer.Name};bodies.Add(layer.Name,body);}body.Boreholes.Add(hole.Id);
                var top=hole.GroundElevation-previous-frame.Elevation;var bottom=hole.GroundElevation-layer.BottomDepth-frame.Elevation;
                foreach(var triangle in triangles){var p=triangle.ExteriorRing.Coordinates.Take(3).Select(c=>new Point3(c.X,c.Y,top)).ToList();Fan(p,bottom,body.Wedges);}
                previous=layer.BottomDepth;
            }
        }
        var result=bodies.Values.Where(b=>b.Volume>1e-8).ToList();
        if(Math.Abs(result.Sum(b=>b.Volume)-expectedVolume)>Math.Max(1e-6,expectedVolume*1e-8))throw new InvalidDataException("시추 지층 삼각분할의 체적이 범위×조사심도와 일치하지 않습니다.");
        return result;
    }
    public static Geometry BoreholeScope(BoreholeSet data,SiteFrame frame)
    {
        data.Validate(frame.Crs);
        var points=data.Boreholes.Select(b=>frame.ToLocal(new(b.Easting,b.Northing,b.GroundElevation))).Select(p=>new Coordinate(p.X,p.Y)).ToArray();
        // Match the compact demonstration worksite, without exposing another margin control.
        return Polygons.Factory.CreateMultiPointFromCoords(points).ConvexHull().Buffer(3,8);
    }
    public static Geometry ResolveScope(TerrainMesh surface,Geometry? requested=null)
    {
        var triangles=requested==null?surface.Triangles:surface.In(requested.EnvelopeInternal);var pieces=triangles.Select(t=>(Geometry)t.Polygon).ToList();
        if(pieces.Count==0)throw new InvalidDataException("선택한 작업대지가 지형 범위와 겹치지 않습니다.");
        var footprint=OverlayNGRobust.Union(pieces);var boundary=requested==null?footprint:OverlayNGRobust.Overlay(footprint,requested,SpatialFunction.Intersection);
        if(boundary.IsEmpty||!boundary.IsValid||boundary.Area<=.01)throw new InvalidDataException("선택한 작업대지가 지형 범위와 겹치지 않습니다.");return boundary;
    }
    public static List<SoilBody> Build(TerrainMesh surface,Geometry site,BoreholeSet data,SiteFrame frame,double excavationEl,double radius,bool calculateExcavation=true,bool limitRadius=true)
    {
        data.Validate(frame.Crs);Guard.Finite(excavationEl,radius);if(radius<=0)throw new InvalidDataException("영향반경은 양수여야 합니다.");
        if(!site.IsValid || site.IsEmpty || site.Area<=.01)throw new InvalidDataException("유효한 작업대지 면 경계가 필요합니다.");
        var cells=Cells(site,data,frame,radius,limitRadius);
        var covered=OverlayNGRobust.Union(cells.Select(c=>c.Cell).ToList());
        if(OverlayNGRobust.Overlay(site,covered,SpatialFunction.Difference).Area>Math.Max(.01,site.Area*1e-6))throw new InvalidDataException("작업대지 일부가 시추공 영향반경 밖입니다. 반경 또는 경계를 확인하세요.");
        var bodies=new Dictionary<string,SoilBody>();var groundCovered=new List<Geometry>();
        foreach(var (borehole,cell) in cells)
        {
            if(calculateExcavation&&excavationEl<borehole.GroundElevation-borehole.Layers[^1].BottomDepth)throw new InvalidDataException($"{borehole.Id}: 굴착 바닥이 최종 조사 심도보다 깊습니다.");
            var previous=0.0;var first=true;
            foreach(var layer in borehole.Layers)
            {
                var top=first?double.PositiveInfinity:borehole.GroundElevation-previous-frame.Elevation;
                var bottom=borehole.GroundElevation-layer.BottomDepth-frame.Elevation;
                if(!bodies.TryGetValue(layer.Name,out var body)){body=new() {Layer=layer.Name};bodies[layer.Name]=body;}body.Boreholes.Add(borehole.Id);
                foreach(var terrain in surface.In(cell.EnvelopeInternal))
                {
                    var footprint=OverlayNGRobust.Overlay(terrain.Polygon,cell,SpatialFunction.Intersection);if(footprint.IsEmpty)continue;
                    if(first)groundCovered.Add(footprint);
                    foreach(var polygon in SoilPolygons(footprint))
                    foreach(var tri in SoilPolygons(PolygonTriangulator.Triangulate(polygon)))
                    {
                        var vertices=tri.ExteriorRing.Coordinates.Take(3).Select(p=>new Point3(p.X,p.Y,terrain.Elevation(p.X,p.Y)??throw new InvalidDataException("TIN 교차 표고 오류."))).ToList();
                        AddWedges(vertices,top,bottom,body.Wedges);
                        if(calculateExcavation)AddWedges(vertices,top,Math.Max(bottom,excavationEl-frame.Elevation),body.Excavation);
                    }
                }
                previous=layer.BottomDepth;first=false;
            }
        }
        if(groundCovered.Count==0 || OverlayNGRobust.Overlay(site,OverlayNGRobust.Union(groundCovered),SpatialFunction.Difference).Area>Math.Max(.02,site.Area*1e-5))throw new InvalidDataException("작업대지 일부가 원본 지형 TIN 밖에 있습니다.");
        return bodies.Values.Where(b=>b.Volume>1e-8).ToList();
    }
    private static List<(Borehole B,Geometry Cell)> Cells(Geometry site,BoreholeSet data,SiteFrame frame,double radius,bool limitRadius)
    {
        var borings=data.Boreholes.Select(b=>(B:b,P:frame.ToLocal(new(b.Easting,b.Northing,b.GroundElevation)))).ToList();
        var cells=new List<(Borehole B,Geometry Cell)>();
        foreach(var item in borings)
        {
            Geometry cell=site;
            foreach(var other in borings.Where(x=>x.B.Id!=item.B.Id))
            {
                var envelope=site.EnvelopeInternal.Copy();envelope.ExpandBy(100);
                var rectangle=new List<Point3> {new(envelope.MinX,envelope.MinY),new(envelope.MaxX,envelope.MinY),new(envelope.MaxX,envelope.MaxY),new(envelope.MinX,envelope.MaxY)};
                var nx=2*(other.P.X-item.P.X);var ny=2*(other.P.Y-item.P.Y);var bound=other.P.X*other.P.X+other.P.Y*other.P.Y-item.P.X*item.P.X-item.P.Y*item.P.Y;
                var clipped=Clip(rectangle,p=>bound-nx*p.X-ny*p.Y);
                if(clipped.Count<3){cell=Polygons.Factory.CreatePolygon();break;}
                cell=OverlayNGRobust.Overlay(cell,Polygons.Factory.CreatePolygon(clipped.Select(p=>new Coordinate(p.X,p.Y)).Append(new(clipped[0].X,clipped[0].Y)).ToArray()),SpatialFunction.Intersection);
                if(cell.IsEmpty)break;
            }
            if(limitRadius&&!cell.IsEmpty)cell=OverlayNGRobust.Overlay(cell,Polygons.Factory.CreatePoint(new Coordinate(item.P.X,item.P.Y)).Buffer(radius,32),SpatialFunction.Intersection);
            if(!cell.IsEmpty)cells.Add((item.B,cell));
        }
        return cells;
    }
    private static IEnumerable<Polygon> SoilPolygons(Geometry geometry)
    {
        if(geometry is Polygon p){if(!p.IsEmpty&&p.Area>1e-12)yield return p;}
        else if(geometry is GeometryCollection)for(var i=0;i<geometry.NumGeometries;i++)foreach(var polygon in SoilPolygons(geometry.GetGeometryN(i)))yield return polygon;
    }
    private static void AddWedges(List<Point3> triangle,double upper,double lower,List<SoilWedge> output)
    {
        if(lower>=upper)return;
        var above=Clip(triangle,p=>p.Z-lower);if(above.Count<3)return;
        if(double.IsPositiveInfinity(upper)){Fan(above,lower,output);return;}
        var terrainPart=Clip(above,p=>upper-p.Z);Fan(terrainPart,lower,output);
        // A flat triangle exactly on the layer top belongs to terrainPart only.
        if(above.Any(p=>p.Z>upper))
        {var capped=Clip(above,p=>p.Z-upper).Select(p=>p with {Z=upper}).ToList();Fan(capped,lower,output);}
    }
    private static void Fan(List<Point3> polygon,double bottom,List<SoilWedge> output)
    {
        for(var i=1;i<polygon.Count-1;i++){var w=new SoilWedge(polygon[0],polygon[i],polygon[i+1],bottom);if(w.Area>1e-7 && w.Volume>1e-8)output.Add(w);}
    }
    private static List<Point3> Clip(List<Point3> polygon,Func<Point3,double> signedDistance)
    {
        var output=new List<Point3>();if(polygon.Count==0)return output;
        var a=polygon[^1];var da=signedDistance(a);
        foreach(var b in polygon)
        {
            var db=signedDistance(b);
            if((da>=0)!=(db>=0))
            {var t=da/(da-db);output.Add(new(a.X+t*(b.X-a.X),a.Y+t*(b.Y-a.Y),a.Z+t*(b.Z-a.Z)));}
            if(db>=0)output.Add(b);a=b;da=db;
        }
        return output.Distinct().ToList();
    }
}
