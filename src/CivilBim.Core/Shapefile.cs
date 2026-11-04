using System.Buffers.Binary;
using System.Text;

namespace CivilBim.Core;

public sealed record ShapeFeature(int Number, int Type, List<List<Point3>> Parts, Dictionary<string,string> Attributes, bool Deleted);
public sealed class ShapeDataset
{
    public string Projection { get; init; } = "";
    public List<ShapeFeature> Features { get; init; } = [];
    public static ShapeDataset Load(string path)
    {
        var prj=Path.ChangeExtension(path,".prj");
        if(!File.Exists(prj)) throw new InvalidDataException("PRJ가 없습니다. 좌표계를 확인한 PRJ를 함께 제공하세요.");
        var projection=File.ReadAllText(prj);
        if(!projection.Contains("PROJCS",StringComparison.OrdinalIgnoreCase) && !projection.Contains("PROJCRS",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("투영좌표계만 지원합니다. 경위도 SHP는 미터 투영좌표로 변환하세요.");
        if(!System.Text.RegularExpressions.Regex.IsMatch(projection,"(?:LENGTHUNIT|UNIT)\\s*\\[\\s*\"(?:metre|meter|metres|meters)\"\\s*,\\s*1(?:\\.0+)?\\s*[,\\]]",System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new InvalidDataException("PRJ의 미터 단위를 확인할 수 없습니다.");
        var dbf=ReadDbf(Path.ChangeExtension(path,".dbf"));
        using var stream=File.OpenRead(path); using var reader=new BinaryReader(stream);
        int BE()=>BinaryPrimitives.ReadInt32BigEndian(reader.ReadBytes(4));
        if(stream.Length<100 || BE()!=9994) throw new InvalidDataException("SHP 헤더 오류.");
        stream.Position=24; var declared=(long)BE()*2;
        if(declared!=stream.Length) throw new InvalidDataException("SHP 파일 길이가 헤더와 다릅니다.");
        if(reader.ReadInt32()!=1000) throw new InvalidDataException("SHP 버전 오류.");
        var headerType=reader.ReadInt32();
        stream.Position=100; var features=new List<ShapeFeature>(); var index=0;
        while(stream.Position<stream.Length)
        {
            if(stream.Length-stream.Position<8) throw new InvalidDataException("SHP 레코드 헤더 잘림.");
            var number=BE(); var length=BE();
            if(length<2 || (long)length*2>stream.Length-stream.Position) throw new InvalidDataException("SHP 레코드 길이 오류.");
            var bytes=reader.ReadBytes(checked(length*2)); using var rr=new BinaryReader(new MemoryStream(bytes));
            var type=rr.ReadInt32(); var parts=new List<List<Point3>>();
            if(type!=0 && type!=headerType) throw new InvalidDataException("SHP 헤더와 레코드 형식 불일치.");
            if(type is 1 or 11) parts.Add([new(rr.ReadDouble(),rr.ReadDouble(),type==11?rr.ReadDouble():0)]);
            else if(type is 3 or 5 or 13 or 15 or 8 or 18)
            {
                rr.BaseStream.Position+=32;
                var np=type is 8 or 18?1:rr.ReadInt32(); var nv=rr.ReadInt32();
                if(np<1 || nv<1 || np>nv || nv>2_000_000) throw new InvalidDataException("SHP 점/파트 수 오류.");
                var starts=type is 8 or 18?new[]{0}:Enumerable.Range(0,np).Select(_=>rr.ReadInt32()).ToArray();
                if(starts[0]!=0 || starts.Any(x=>x<0 || x>=nv) || starts.Zip(starts.Skip(1)).Any(x=>x.First>=x.Second)) throw new InvalidDataException("SHP 파트 인덱스 오류.");
                if((long)nv*16>rr.BaseStream.Length-rr.BaseStream.Position) throw new InvalidDataException("SHP 좌표 잘림.");
                var points=Enumerable.Range(0,nv).Select(_=>new Point3(rr.ReadDouble(),rr.ReadDouble())).ToArray();
                if(type is 13 or 15 or 18)
                {
                    if((long)nv*8+16>rr.BaseStream.Length-rr.BaseStream.Position) throw new InvalidDataException("SHP Z 좌표 잘림.");
                    rr.BaseStream.Position+=16;
                    for(var i=0;i<nv;i++) points[i]=points[i] with {Z=rr.ReadDouble()};
                }
                for(var i=0;i<np;i++) parts.Add(points[starts[i]..(i+1<np?starts[i+1]:nv)].ToList());
            }
            else if(type!=0) throw new InvalidDataException($"지원하지 않는 SHP 형식 {type}.");
            foreach(var p in parts.SelectMany(x=>x)) Guard.Finite(p.X,p.Y,p.Z);
            var row=index<dbf.Count?dbf[index]:(new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase),false);
            features.Add(new(number,type,parts,row.Item1,row.Item2)); index++;
        }
        if(dbf.Count>0 && dbf.Count!=features.Count) throw new InvalidDataException("SHP/DBF 레코드 수 불일치.");
        return new() {Projection=projection,Features=features};
    }
    private static List<(Dictionary<string,string>,bool)> ReadDbf(string path)
    {
        if(!File.Exists(path)) return [];
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding=Encoding.GetEncoding(949); var cpg=Path.ChangeExtension(path,".cpg");
        if(File.Exists(cpg))
        {
            var s=File.ReadAllText(cpg).Trim().Trim('\ufeff');
            encoding=s.Equals("UTF-8",StringComparison.OrdinalIgnoreCase)?Encoding.UTF8:int.TryParse(s,out var n)?Encoding.GetEncoding(n):Encoding.GetEncoding(s);
        }
        using var stream=File.OpenRead(path); using var r=new BinaryReader(stream);
        if(stream.Length<33) throw new InvalidDataException("DBF 헤더 잘림.");
        stream.Position=4; var count=r.ReadInt32(); var header=r.ReadUInt16(); var record=r.ReadUInt16();
        if(count<0 || header<33 || record<1 || (long)header+(long)count*record>stream.Length) throw new InvalidDataException("DBF 길이 오류.");
        stream.Position=32; var fields=new List<(string Name,int Width)>();
        while(stream.Position<header-1)
        {
            var desc=r.ReadBytes(32); if(desc.Length!=32) throw new InvalidDataException("DBF 필드 잘림.");
            fields.Add((encoding.GetString(desc,0,11).TrimEnd('\0'),desc[16]));
        }
        if(fields.Sum(x=>x.Width)+1!=record || r.ReadByte()!=13) throw new InvalidDataException("DBF 필드 길이 오류.");
        var rows=new List<(Dictionary<string,string>,bool)>();
        for(var i=0;i<count;i++)
        {
            stream.Position=header+(long)i*record; var deleted=r.ReadByte()==42;
            var row=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(var f in fields) row.Add(f.Name,encoding.GetString(r.ReadBytes(f.Width)).Trim());
            rows.Add((row,deleted));
        }
        return rows;
    }
    public List<Point3> TerrainPoints(string elevationField, double spacing, int maxPoints=20000)
    {
        Guard.Finite(spacing); if(spacing<=0) throw new InvalidDataException("샘플 간격은 양수여야 합니다.");
        var result=new Dictionary<(long,long),Point3>();
        void Add(Point3 p)
        {
            if(p.Z < -1e38)throw new InvalidDataException("SHP 표고에 NoData 값이 있습니다.");
            var key=((long)Math.Round(p.X/0.001),(long)Math.Round(p.Y/0.001));
            if(result.TryGetValue(key,out var old) && Math.Abs(old.Z-p.Z)>0.05) throw new InvalidDataException("같은 XY에 상충하는 표고가 있습니다.");
            result[key]=p;
            if(result.Count>maxPoints) throw new InvalidDataException($"지형점 {maxPoints}개 초과. 범위를 줄이거나 간격을 늘리세요.");
        }
        foreach(var f in Features.Where(x=>!x.Deleted && x.Type!=0))
        {
            if(f.Type is 5 or 15) throw new InvalidDataException("지형에는 등고선/표고점 SHP를 선택하세요. 면 SHP는 지원하지 않습니다.");
            double? z=null;
            if(!string.IsNullOrWhiteSpace(elevationField))
            {
                if(!f.Attributes.TryGetValue(elevationField,out var raw)) throw new InvalidDataException($"표고 필드 {elevationField} 누락.");
                z=Guard.Number(raw); Guard.Finite(z.Value);
            }
            else if(f.Type is not (11 or 13 or 18)) throw new InvalidDataException("2D SHP는 DBF 표고 필드가 필요합니다.");
            foreach(var part in f.Parts)
            {
                for(var i=0;i<part.Count;i++)
                {
                    var p=z.HasValue?part[i] with {Z=z.Value}:part[i]; Add(p);
                    if(i==0) continue;
                    var a=z.HasValue?part[i-1] with {Z=z.Value}:part[i-1]; var n=(int)Math.Ceiling(a.DistanceXY(p)/spacing);
                    if(n>maxPoints) throw new InvalidDataException("너무 긴 선분/작은 간격.");
                    for(var j=1;j<n;j++) {var t=(double)j/n; Add(new(a.X+t*(p.X-a.X),a.Y+t*(p.Y-a.Y),a.Z+t*(p.Z-a.Z)));}
                }
            }
        }
        if(result.Count<3) throw new InvalidDataException("지형점 3개 이상 필요.");
        return result.Values.ToList();
    }
}
