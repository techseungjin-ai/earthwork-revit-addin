using Autodesk.Revit.DB;
using CivilBim.Core;

namespace CivilBim.Revit;

public sealed record CadPath(string Layer,List<XYZ> Points,bool Closed,string BlockKey="");
public sealed record CadBlock(string Layer,XYZ Origin,double Angle,string Key);
public sealed record CadCircle(string Layer,XYZ Center,double Radius);
public sealed class CadReader
{
    public List<CadPath> Paths {get;}=[];
    public List<CadCircle> Circles {get;}=[];
    public List<CadBlock> Blocks {get;}=[];
    public string RegionKey {get;private set;}="all";
    public string[] Layers=>Paths.Select(x=>x.Layer).Concat(Circles.Select(x=>x.Layer)).Concat(Blocks.Select(x=>x.Layer)).Distinct().Order().ToArray();
    public void Crop(XYZ a,XYZ b)
    {
        bool Inside(XYZ p)=>p.X>=Math.Min(a.X,b.X)&&p.X<=Math.Max(a.X,b.X)&&p.Y>=Math.Min(a.Y,b.Y)&&p.Y<=Math.Max(a.Y,b.Y);
        Circles.RemoveAll(c=>!Inside(c.Center));Paths.RemoveAll(p=>!p.Points.All(Inside));
        Blocks.RemoveAll(b=>!Inside(b.Origin));
        RegionKey=FormattableString.Invariant($"{Math.Min(a.X,b.X):F3},{Math.Min(a.Y,b.Y):F3},{Math.Max(a.X,b.X):F3},{Math.Max(a.Y,b.Y):F3}");
    }
    public static CadReader Read(Document doc,ImportInstance import)
    {
        var data=new CadReader();var arcs=new List<(string Layer,XYZ Center,double Radius,double Length)>();
        void Walk(GeometryElement geometry,Transform transform,string inherited="",string key="",int depth=0)
        {
            var index=0;
            foreach(var g in geometry)
            {
                var layer=(doc.GetElement(g.GraphicsStyleId) as GraphicsStyle)?.GraphicsStyleCategory?.Name??inherited;
                if(layer=="0" && inherited.Length>0)layer=inherited;
                var child=key+"/"+index++;
                if(g is GeometryInstance instance)
                {
                    var absolute=transform.Multiply(instance.Transform);
                    if(depth>0)data.Blocks.Add(new(layer,absolute.Origin,Math.Atan2(absolute.BasisX.Y,absolute.BasisX.X),child));
                    Walk(instance.GetSymbolGeometry(),absolute,layer,child,depth+1);continue;
                }
                if(g is Line line) data.Paths.Add(new(layer,[transform.OfPoint(line.GetEndPoint(0)),transform.OfPoint(line.GetEndPoint(1))],false,key));
                else if(g is PolyLine poly)
                {
                    var p=poly.GetCoordinates().Select(transform.OfPoint).ToList();
                    data.Paths.Add(new(layer,p,p.Count>2 && p[0].DistanceTo(p[^1])<.001,key));
                }
                else if(g is Arc arc)
                {
                    var normal=transform.OfVector(arc.Normal).Normalize();
                    if(Math.Abs(normal.Z)>.99999)
                    {
                        var center=transform.OfPoint(arc.Center);var radius=transform.OfVector(arc.XDirection.Multiply(arc.Radius)).GetLength();
                        arcs.Add((layer,center,radius,arc.IsBound?arc.Length:2*Math.PI*arc.Radius));
                    }
                }
            }
        }
        Walk(import.get_Geometry(new Options {IncludeNonVisibleObjects=true}),Transform.Identity);
        // 반원 둘로 수입된 원을 병합. 열린 호 하나는 말뚝으로 인식하지 않음.
        foreach(var group in arcs.GroupBy(a=>(a.Layer,X:Math.Round(a.Center.X,4),Y:Math.Round(a.Center.Y,4),Z:Math.Round(a.Center.Z,4),R:Math.Round(a.Radius,4))))
        {
            var a=group.First();if(Math.Abs(group.Sum(x=>x.Length)-2*Math.PI*a.Radius)<.01)
                data.Circles.Add(new(a.Layer,a.Center,a.Radius));
        }
        return data;
    }
    public List<XYZ> PileCenters(string layer,bool centerline,double spacing)
    {
        var points=new List<XYZ>();
        if(!centerline) points.AddRange(Circles.Where(c=>c.Layer==layer).Select(c=>c.Center));
        else
        {
            if(spacing<=0) throw new InvalidDataException("CIP 간격은 양수여야 합니다.");
            foreach(var path in Paths.Where(p=>p.Layer==layer))
            {
                var until=0.0;
                for(var i=1;i<path.Points.Count;i++)
                {
                    var a=path.Points[i-1];var b=path.Points[i];var distance=a.DistanceTo(b);if(distance<1e-8)continue;
                    while(until<distance-1e-7)
                    {
                        points.Add(a+(b-a).Multiply(until/distance));until+=spacing;
                        if(points.Count>10000) throw new InvalidDataException("CIP 10000개 초과. 레이어/간격을 확인하세요.");
                    }
                    until-=distance;
                }
            }
        }
        return points.DistinctBy(p=>(Math.Round(p.X,4),Math.Round(p.Y,4))).ToList();
    }
    public List<PileSymbol> HPiles(string layer)
    {
        var blocks=Blocks.Where(b=>b.Layer==layer).ToList();
        blocks=blocks.Where(b=>!blocks.Any(parent=>parent.Key!=b.Key && b.Key.StartsWith(parent.Key+"/",StringComparison.Ordinal))).ToList();
        var result=new List<PileSymbol>();
        foreach(var block in blocks)
        {
            var outline=new List<PlanSegment>();
            foreach(var path in Paths.Where(p=>p.BlockKey==block.Key||p.BlockKey.StartsWith(block.Key+"/",StringComparison.Ordinal)))
                for(int j=1;j<path.Points.Count;j++)outline.Add(new(ModelBuilder.Meters(path.Points[j-1]),ModelBuilder.Meters(path.Points[j])));
            var recognized=PlanRecognition.HPiles(outline);
            if(recognized.Count>0)result.AddRange(recognized);
            else result.Add(new(ModelBuilder.Meters(block.Origin),PlanRecognition.Normalize(block.Angle),"CAD 블록"));
        }
        var segments=new List<PlanSegment>();
        foreach(var path in Paths.Where(p=>p.Layer==layer && !blocks.Any(b=>p.BlockKey.StartsWith(b.Key,StringComparison.Ordinal))))
            for(var i=1;i<path.Points.Count;i++)segments.Add(new(ModelBuilder.Meters(path.Points[i-1]),ModelBuilder.Meters(path.Points[i])));
        result.AddRange(PlanRecognition.HPiles(segments));return result.DistinctBy(p=>(Math.Round(p.Center.X,4),Math.Round(p.Center.Y,4))).ToList();
    }
}
