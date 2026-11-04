using CivilBim.Core;

public static class LocalFlowChecks
{
    public static void Run()
    {
        const string first="PROJCS[\"A\",GEOGCS[\"GCS_Korea 2000\",DATUM[\"D_Korea_2000\",SPHEROID[\"GRS_1980\",6378137,298.257222101]],PRIMEM[\"Greenwich\",0],UNIT[\"Degree\",0.017453292519943295]],PROJECTION[\"Transverse_Mercator\"],PARAMETER[\"central_meridian\",127],PARAMETER[\"false_northing\",600000],UNIT[\"Meter\",1]]";
        var second=first.Replace("\"A\"","\"B\"").Replace("GCS_Korea 2000","GCS_Korea_2000").Replace("6378137,","6378137.0,").Replace("0.017453292519943295","0.0174532925199433").Replace("PARAMETER[\"central_meridian\",127],PARAMETER[\"false_northing\",600000]","PARAMETER[\"False_Northing\",600000.0],PARAMETER[\"Central_Meridian\",127.0]");
        if(!ProjectionComparison.Equivalent(first,second))throw new Exception("WKT 표시명/순서/대소문자/실수 표기 동등성 실패");
        foreach(var different in new[]{first.Replace("600000","500000"),first.Replace("127]","129]"),first.Replace("298.257222101","298.3"),first.Replace("Meter\",1","Meter\",0.3048"),""})
            if(ProjectionComparison.Equivalent(first,different))throw new Exception("실제 투영계 차이/누락을 허용함");
        Console.WriteLine("PASS PRJ 표현차이 허용, 투영원점/중앙경선/타원체/단위 차이·누락 거부");
        var wkt=new NetTopologySuite.IO.WKTReader();
        var roads=RoadBoundary.Merge(new[]{wkt.Read("POLYGON((0 0,10 0,10 5,0 5,0 0))"),wkt.Read("POLYGON((10 0,20 0,20 5,10 5,10 0))")},.0008);
        if(!roads.IsValid||Math.Abs(roads.Area-100)>.01||Polygons.Enumerate(roads).Count()!=1)throw new Exception("도로 경계 병합/면적 보존 실패");
        var hole=RoadBoundary.Merge(new[]{wkt.Read("POLYGON((0 0,20 0,20 20,0 20,0 0),(5 5,5 10,10 10,10 5,5 5))")},.0008);
        if(Math.Abs(hole.Area-375)>.03||Polygons.Enumerate(hole).Single().NumInteriorRings!=1)throw new Exception("도로 구멍 보존 실패");
        var tangent=RoadBoundary.Merge(new[]{wkt.Read("POLYGON((0 0,10 0,10 10,0 10,0 0),(0 5,2 4,2 6,0 5))")},.0008);
        if(!tangent.IsValid||Polygons.Enumerate(tangent).Any(p=>p.InteriorRings.Any(r=>r.Distance(p.ExteriorRing)<=1e-9)))throw new Exception("Revit 비허용 접점 경계 정리 실패");
        Console.WriteLine("PASS 도로 접합 경계/구멍/면적 보존");
        var memberProfile=new MemberDepthProfile(new[]{new PlanSegment(new(100,0,12),new(100,0,-8)),new PlanSegment(new(120,0,10),new(120,0,-12))},new(1,0),new(0,0,1),new(),0);
        var height=memberProfile.At(5);if(Math.Abs(height.Top-11.5)>1e-8||Math.Abs(height.Bottom+9)>1e-8)throw new Exception("수직 말뚝선 깊이 보간 실패");
        var flatProfile=new MemberDepthProfile(new[]{new PlanSegment(new(100,42),new(100,22)),new PlanSegment(new(120,40),new(120,18))},new(1,0),new(0,1),new(0,30),0);
        if(flatProfile.At(5)!=height)throw new Exception("XY 전개도 EL 한점 대응 실패");
        var reversed=new MemberDepthProfile(new[]{new PlanSegment(new(100,0,12),new(100,0,-8)),new PlanSegment(new(120,0,10),new(120,0,-12))},new(1,0),new(0,0,1),new(),0,true);
        if(Math.Abs(reversed.At(5).Top-10.5)>1e-8)throw new Exception("수직 말뚝선 전개방향 반전 실패");
        try{memberProfile.At(21);throw new Exception("전개범위외 대응을 허용함");}catch(InvalidDataException){}
        Console.WriteLine("PASS 수직 말뚝선 실제Z/XY EL한점/보간/반전/범위외 거부");
        var dwgRoot=Environment.GetEnvironmentVariable("CIVILBIM_DWG_CHECK_ROOT");
        if(!string.IsNullOrWhiteSpace(dwgRoot))
        {
            var segments=new List<PlanSegment>();var pattern=@"\((10|11) ([-+0-9.eE]+) ([-+0-9.eE]+) ([-+0-9.eE]+)\)";
            foreach(var line in File.ReadLines(Path.Combine(dwgRoot,"development","entities.txt")))
            {
                if(!line.Contains("(0 . \"LINE\")")||!line.Contains("(8 . \"_측면말뚝\")"))continue;var matches=System.Text.RegularExpressions.Regex.Matches(line,pattern);if(matches.Count!=2)continue;
                Point3 Read(int i)=>new(double.Parse(matches[i].Groups[2].Value,System.Globalization.CultureInfo.InvariantCulture)/1000,double.Parse(matches[i].Groups[3].Value,System.Globalization.CultureInfo.InvariantCulture)/1000);
                segments.Add(new(Read(0),Read(1)));
            }
            var actual=new MemberDepthProfile(segments,new(1,0),new(0,1),new(0,12.5908),-13.96);
            if(segments.Count!=181||actual.Members.Count!=179||Math.Abs(actual.Length-264.021)>.005)throw new Exception($"실제 전개도 원본181선/중복제외179위치/264.021m 대응 실패: 입력{segments.Count}, 인식{actual.Members.Count}, 폭{actual.Length:F6}m");
            Console.WriteLine($"PASS 실제 전개도 수직말뚝 {actual.Members.Count}개 / 폭{actual.Length:F3}m");
        }
        var dense=Enumerable.Range(0,524288).Select(i=>new Point3(i%1024,i/1024,(i%1024)*.01+(i/1024)*.02)).ToList();
        var largeShape=new ShapeDataset {Features=[new(1,13,Enumerable.Range(0,512).Select(i=>dense.Skip(i*1024).Take(1024).ToList()).ToList(),new(),false)]};
        var retained=largeShape.TerrainPoints("",5,2000000);var original=dense.ToHashSet();
        if(retained.Count!=dense.Count||retained.Any(p=>!original.Contains(p)))throw new Exception("대규모 SHP 원본 XYZ 유지 실패");
        Console.WriteLine($"PASS SHP 원본 {dense.Count}점 전체 XYZ 유지 (강제20,000점 축소 제거)");
        var prjRoot=Environment.GetEnvironmentVariable("CIVILBIM_PRJ_CHECK_ROOT");
        if(!string.IsNullOrWhiteSpace(prjRoot))
        {
            var files=Directory.GetFiles(prjRoot,"*.prj",SearchOption.AllDirectories);
            if(files.Length<2)throw new Exception("실제 PRJ 비교자료 부족");
            var baseline=File.ReadAllText(files[0]);foreach(var file in files)ProjectionComparison.RequireSame(baseline,File.ReadAllText(file),file);
            Console.WriteLine($"PASS 실제 혼합 축척 PRJ {files.Length}개 정의 동등성");
        }
        var frame=new SiteFrame("TEST",200000,500000,7,32);var pivot=new Point3(12,-9);var moved=frame.RotateAbout(pivot,-47);var p=new Point3(200031,500006,15);var old=frame.ToLocal(p);var a=-47*Math.PI/180;var expected=new Point3(pivot.X+(old.X-pivot.X)*Math.Cos(a)-(old.Y-pivot.Y)*Math.Sin(a),pivot.Y+(old.X-pivot.X)*Math.Sin(a)+(old.Y-pivot.Y)*Math.Cos(a),old.Z);
        if(moved.ToLocal(p).DistanceXY(expected)>1e-8||moved.ToSurvey(expected).DistanceXY(p)>1e-8||moved.Elevation!=7)throw new Exception("회전 중심의 Frame/시추 좌표 보존 실패");
        foreach(var angle in new[]{0.0,37,90})
        {
            var r=angle*Math.PI/180;Point3 P(double station,double z)=>new(100+station*Math.Cos(r),30+station*Math.Sin(r),z);
            var profile=new VerticalProfile([new(P(0,12),P(20,10))],[new(P(0,-8),P(20,-12))]);profile.CheckLength(20);var result=profile.At(5);
            if(Math.Abs(result.Top-11.5)>1e-8||Math.Abs(result.Bottom+9)>1e-8)throw new Exception("전개도 실제Z/회전된 수직면 거리 대응 실패");
            var reverse=new VerticalProfile([new(P(0,12),P(20,10))],[new(P(0,-8),P(20,-12))],true).At(5);
            if(Math.Abs(reverse.Top-10.5)>1e-8||Math.Abs(reverse.Bottom+11)>1e-8)throw new Exception("전개도 진행방향 반전 실패");
        }
        try{new VerticalProfile([new(new(0,0),new(10,0))],[new(new(0,1),new(10,1))]);throw new Exception("눕힌 전개도를 거부하지 않음");}catch(InvalidDataException){}
        var sample=new VerticalProfile([new(new(0,0,10),new(10,0,10))],[new(new(0,0,-10),new(10,0,-10))]);
        try{sample.CheckLength(20);throw new Exception("경로길이 불일치를 거부하지 않음");}catch(InvalidDataException){}
        Console.WriteLine("PASS 임의 회전중심 Frame/시추 보존, 실제Z 전개도 0/37/90도·반전·눕힌도면·길이불일치 검사");
    }
}
