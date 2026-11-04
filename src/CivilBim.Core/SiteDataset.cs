using System.Globalization;
using System.Text.RegularExpressions;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Simplify;

namespace CivilBim.Core;

public sealed record BuildingFootprint(string Id,Geometry Footprint,double Height);
public sealed class SiteDataset
{
    public SiteFrame Frame {get;private set;}=new("",0,0);
    public List<Point3> Points {get;private set;}=[];
    public Geometry Roads {get;private set;}=Polygons.Factory.CreatePolygon();
    public List<BuildingFootprint> Buildings {get;}=[];
    public List<string> Log {get;}=[];
    public static SiteDataset Load(string root,int scale,double spacing,double floorHeight=3.3,SiteFrame? frame=null,double rotationDegrees=0,Point3? rotationPivot=null)
    {
        if(!Directory.Exists(root))throw new InvalidDataException("SHP 묶음 폴더를 지정하세요.");
        if(scale<1 || spacing<=0 || floorHeight<=0)throw new InvalidDataException("축척/샘플 간격/층고를 확인하세요.");
        Guard.Finite(rotationDegrees);
        var result=new SiteDataset();
        var all=Directory.GetFiles(root,"*.shp",SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToList();
        List<string> Find(string code)=>all.Where(p=>Regex.IsMatch(Path.GetFileNameWithoutExtension(p),$"(?:^|_){code}$",RegexOptions.IgnoreCase)).ToList();
        var contours=Find("F0010000");if(contours.Count==0)throw new InvalidDataException("하위 폴더에서 등고선 *F0010000.shp를 찾지 못했습니다.");
        var terrain=contours.Select(ShapeDataset.Load).ToList();
        var projection=terrain[0].Projection.Trim();for(var i=0;i<terrain.Count;i++)ProjectionComparison.RequireSame(projection,terrain[i].Projection,contours[i]);
        var raw=terrain.SelectMany(d=>d.Features).SelectMany(f=>f.Parts).SelectMany(p=>p).ToList();if(raw.Count==0)throw new InvalidDataException("등고선 점이 없습니다.");
        result.Frame=frame??new(ProjectionId(projection),(raw.Min(p=>p.X)+raw.Max(p=>p.X))/2,(raw.Min(p=>p.Y)+raw.Max(p=>p.Y))/2,0,rotationDegrees);
        result.Frame.Validate();
        // Repair/simplify topology in one fixed orientation. Rotate the completed dataset
        // once so rotation cannot change which near-collinear vertices are retained.
        var readingFrame=result.Frame with {RotationDegrees=0};
        if(rotationPivot is Point3 pivot)result.Frame=readingFrame.RotateAbout(pivot,result.Frame.RotationDegrees);
        var survey=new List<Point3>();
        foreach(var d in terrain)
        {
            var field=Field(d,"등고수치","등고선","표고","ELEVATION","ELEV","HEIGHT","Z");
            survey.AddRange(d.TerrainPoints(field,spacing,2000000));
        }
        var spot=Find("F0020000");
        foreach(var file in spot)
        {
            var d=ShapeDataset.Load(file);ProjectionComparison.RequireSame(projection,d.Projection,file);
            var field=Field(d,"표고수치","표고","표고값","높이","ELEVATION","ELEV","HEIGHT","Z");
            foreach(var f in d.Features.Where(f=>!f.Deleted))
            foreach(var p in f.Parts.SelectMany(p=>p))
            {
                var z=p.Z;
                if(field.Length>0 && f.Attributes.TryGetValue(field,out var value) && double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out var n))z=n;
                else if(f.Type is not (11 or 18))continue;
                survey.Add(p with {Z=z});
            }
        }
        var unique=new Dictionary<(long,long),Point3>();
        foreach(var p in survey)
        {
            var q=readingFrame.ToLocal(p);var key=((long)Math.Round(q.X*1000),(long)Math.Round(q.Y*1000));
            if(unique.TryGetValue(key,out var old)&&Math.Abs(old.Z-q.Z)>.1)throw new InvalidDataException("도엽 경계/표고점의 같은 위치에 표고가 충돌합니다.");unique[key]=q;
        }
        result.Points=unique.Values.ToList();
        result.Log.Add($"원본 입력점 {result.Points.Count:N0}개 전체 유지. 자동 점 삭제 없음. 사용자 지정 감소율은 Revit 지형 솔리드 단순화에서만 적용합니다.");
        result.Log.Add($"등고선 {contours.Count}개 도엽, 표고점 {spot.Count}개 파일, 입력 {unique.Count}점 → 생성 {result.Points.Count}점. 원본 1:{scale}; 좌표 배율 1.");
        result.Log.Add($"좌표계 {result.Frame.Crs}, 자동 원점 E={result.Frame.Easting:F3}, N={result.Frame.Northing:F3}");
        result.Log.Add($"대지 회전 {result.Frame.RotationDegrees:F3}° (평면 위에서 +반시계 / -시계). SHP와 시추공에 동일한 원점·회전 적용. 공유좌표 변경 없음.");
        var roads=Find("A0010000").Where(p=>Path.GetFileName(p).Contains("A_",StringComparison.OrdinalIgnoreCase)).ToList();
        if(roads.Count==0)roads=Find("A0010000").Where(p=>ShapeTypeOf(p) is 5 or 15).ToList();
        var roadGeometries=new List<Geometry>();
        foreach(var file in roads)
        {
            var d=ShapeDataset.Load(file);ProjectionComparison.RequireSame(projection,d.Projection,file);
            foreach(var f in d.Features.Where(f=>!f.Deleted && f.Type is 5 or 15))
            {
                try {var g=Polygons.FromFeature(f,readingFrame);if(!g.IsEmpty)roadGeometries.Add(g);}
                catch(Exception ex){result.Log.Add($"도로 레코드 {f.Number} 제외: {ex.Message}");}
            }
        }
        if(roadGeometries.Count>0)result.Roads=TopologyPreservingSimplifier.Simplify(UnaryUnionOp.Union(roadGeometries),.01);
        result.Log.Add($"도로 면 {roadGeometries.Count}개 → 중복/접합 정리 {Polygons.Enumerate(result.Roads).Count()}개 구획.");
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var file in Find("B0010000"))
        {
            var d=ShapeDataset.Load(file);ProjectionComparison.RequireSame(projection,d.Projection,file);
            foreach(var f in d.Features.Where(f=>!f.Deleted && f.Type is 5 or 15))
            {
                try
                {
                    var g=Polygons.FromFeature(f,readingFrame);if(g.IsEmpty)continue;
                    var id=f.Attributes.GetValueOrDefault("UFID","").Trim();if(id.Length==0)id=g.Normalized().AsText();if(!seen.Add(id))continue;
                    var floors=Number(f,"층수","지상층","지상층수","FLOORS","NUM_FLOORS");
                    var height=Number(f,"건물높이","높이","HEIGHT","HGT");
                    if(height<=0)height=(floors>0?floors:1)*floorHeight;
                    if(height>600)throw new InvalidDataException("건물 높이가 600m를 넘습니다. 높이 단위/필드를 확인하세요.");
                    result.Buildings.Add(new(id,g,height));
                }
                catch(Exception ex){result.Log.Add($"건물 레코드 {f.Number} 제외: {ex.Message}");}
            }
        }
        if(result.Frame.RotationDegrees!=0)
        {
            var center=rotationPivot??new Point3();
            result.Points=result.Points.Select(p=>result.Frame.ToLocal(readingFrame.ToSurvey(p))).ToList();
            var transform=AffineTransformation.RotationInstance(result.Frame.RotationDegrees*Math.PI/180,center.X,center.Y);
            result.Roads=transform.Transform(result.Roads);
            for(var i=0;i<result.Buildings.Count;i++)result.Buildings[i]=result.Buildings[i] with {Footprint=transform.Transform(result.Buildings[i].Footprint)};
        }
        result.Log.Add($"주변 건물 {result.Buildings.Count}동 (실 높이 필드 우선, 없으면 층수×{floorHeight}m).");return result;
    }
    private static int ShapeTypeOf(string p){using var f=File.OpenRead(p);using var r=new BinaryReader(f);f.Position=32;return r.ReadInt32();}
    private static string Field(ShapeDataset d,params string[] names)=>names.FirstOrDefault(n=>d.Features.Any(f=>f.Attributes.ContainsKey(n)))??"";
    private static double Number(ShapeFeature f,params string[] names)
    {
        foreach(var n in names)if(f.Attributes.TryGetValue(n,out var s)&&double.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out var v)&&double.IsFinite(v))return v;return 0;
    }
    public static string ProjectionId(string wkt)
    {
        var match=Regex.Matches(wkt,@"AUTHORITY\s*\[\s*""EPSG""\s*,\s*""(\d+)""\s*\]",RegexOptions.IgnoreCase);
        if(match.Count>0)return "EPSG:"+match[^1].Groups[1].Value;
        if(wkt.Contains("Korea_2000_Central_Belt_2010",StringComparison.OrdinalIgnoreCase))return "EPSG:5186";
        return "WKT:"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(wkt)))[..16];
    }
}

public static class Polygons
{
    public static GeometryFactory Factory {get;}=new(new PrecisionModel());
    public static Geometry FromFeature(ShapeFeature feature,SiteFrame frame)
    {
        Geometry output=Factory.CreatePolygon();
        foreach(var part in feature.Parts)
        {
            var points=part.Select(frame.ToLocal).Select(p=>new Coordinate(p.X,p.Y)).DistinctConsecutive().ToList();
            if(points.Count<3)continue;if(!points[0].Equals2D(points[^1]))points.Add(points[0].Copy());
            var ring=Factory.CreatePolygon(points.ToArray());var clean=GeometryFixer.Fix(ring);
            if(clean.Area>.01)output=output.SymmetricDifference(clean);
        }
        return TopologyPreservingSimplifier.Simplify(GeometryFixer.Fix(output),.005);
    }
    public static IEnumerable<Polygon> Enumerate(Geometry g)
    {
        if(g is Polygon p){if(!p.IsEmpty && p.Area>.01)yield return p;}
        else for(var i=0;i<g.NumGeometries;i++)foreach(var part in Enumerate(g.GetGeometryN(i)))yield return part;
    }
    private static IEnumerable<Coordinate> DistinctConsecutive(this IEnumerable<Coordinate> points)
    {Coordinate? last=null;foreach(var p in points){if(last==null || !p.Equals2D(last)){yield return p;last=p;}}}
}
