using CivilBim.Core;
using System.Text.Json;

int checks=0;
{
    var h=new[]{new Point3(-.15,-.15),new(.15,-.15),new(.15,-.13),new(.01,-.13),new(.01,.13),new(.15,.13),new(.15,.15),new(-.15,.15),new(-.15,.13),new(-.01,.13),new(-.01,-.13),new(-.15,-.13),new(-.15,-.15)};
    foreach(var angle in new[]{0.0,Math.PI/4,Math.PI/2})
    {
        var outline=h.Select(pt=>new Point3(pt.X*Math.Cos(angle)-pt.Y*Math.Sin(angle),pt.X*Math.Sin(angle)+pt.Y*Math.Cos(angle))).ToList();
        var result=PlanRecognition.HPiles(outline.Zip(outline.Skip(1),(a,b)=>new PlanSegment(a,b)));
        if(result.Count!=1||Math.Abs(Math.Sin(result[0].Angle-angle))>1e-8)throw new Exception("폐합 H단면의 플랜지 방향 검산 실패");
    }
}
{
    var factory=Polygons.Factory;
    var lineBoundary=ConnectedPath.Join(new IReadOnlyList<Point3>[] {new[]{new Point3(10,0),new Point3(10,10)},new[]{new Point3(0,10),new Point3(0,0)},new[]{new Point3(10,10),new Point3(0,10)},new[]{new Point3(0,0),new Point3(10,0)}});
    if(lineBoundary.Count!=5||lineBoundary[0].DistanceXY(lineBoundary[^1])>.001)throw new Exception("여러 Revit 선의 폐합 외곽 연결 실패");
    var boundary=factory.CreatePolygon(new[]{new NetTopologySuite.Geometries.Coordinate(0,0),new(10,0),new(10,10),new(0,10),new(0,0)});
    var regions=ExcavationRegions.Split(new IReadOnlyList<Point3>[] {new[]{new Point3(5,0),new Point3(5,10)},new[]{new Point3(0,5),new Point3(10,5)}},boundary);
    if(regions.Count!=4||Math.Abs(regions.Sum(p=>p.Area)-100)>1e-7||regions.Any(p=>Math.Abs(p.Area-25)>1e-7))throw new Exception("교차 구분선의 폐합 영역 분할 실패");
    var disconnected=ExcavationRegions.Split(new IReadOnlyList<Point3>[] {new[]{new Point3(5,0),new Point3(5,9)}},boundary);
    if(disconnected.Count!=1)throw new Exception("끊긴 선을 임의로 연결했습니다.");
    var gapPaths=new IReadOnlyList<Point3>[] {new[]{new Point3(5,0),new Point3(5,9.998)}};
    if(ExcavationRegions.Split(gapPaths,boundary,.003).Count!=2)throw new Exception("3mm 허용에서 2mm 틈이 연결되지 않았습니다.");
    if(ExcavationRegions.Split(gapPaths,boundary,.001).Count!=1)throw new Exception("허용 범위 밖 틈을 연결했습니다.");
    var endGap=new IReadOnlyList<Point3>[] {new[]{new Point3(5,0),new Point3(5,4.999)},new[]{new Point3(5,5.001),new Point3(5,10)}};
    if(ExcavationRegions.Split(endGap,boundary,.003).Count!=2)throw new Exception("분리된 선 끝점의 2mm 틈 연결 실패");
    if(ExcavationRegions.Split(endGap,boundary,.001).Count!=1)throw new Exception("허용 범위 밖 선 끝점을 연결했습니다.");
    var file=Environment.GetEnvironmentVariable("CIVILBIM_EXC_PATHS");
    if(!string.IsNullOrEmpty(file))
    {
        using var json=JsonDocument.Parse(File.ReadAllText(file));var paths=json.RootElement.GetProperty("Paths").EnumerateArray().Where(x=>x.GetProperty("Layer").GetString()=="CE-SLOP-FILL").Select(x=>(IReadOnlyList<Point3>)x.GetProperty("Points").EnumerateArray().Select(v=>new Point3(v.GetProperty("X").GetDouble(),v.GetProperty("Y").GetDouble(),0)).ToList()).ToList();
        var outer=factory.CreatePolygon(new[]{new NetTopologySuite.Geometries.Coordinate(99,17),new(149,17),new(149,79),new(99,79),new(99,17)});
        var actual=ExcavationRegions.Split(paths,outer,.002);Console.WriteLine($"실제 CAD 사면선 {paths.Count}개 (허용 2mm) → 폐합 구역 {actual.Count}개 / 합계 {actual.Sum(p=>p.Area):F3}㎡");
    }
    Console.WriteLine("PASS CAD 사면선 교차 분할 및 끊어진 경계 검사");
}
LocalFlowChecks.Run();
if(args.Length==2&&args[0]=="--large-site")
{
    var site=SiteDataset.Load(args[1],5000,5,rotationDegrees:-86.037,rotationPivot:new Point3(-1.830,-1.177));
    if(site.Points.Count<=20000||site.Log.All(l=>!l.Contains("전체 유지")))throw new Exception("대규모 실제 SHP 전체 점 유지 실패");
    var unrotated=SiteDataset.Load(args[1],5000,5);var pivot=new Point3(-1.830,-1.177);var expectedFrame=unrotated.Frame.RotateAbout(pivot,-86.037);
    if(Math.Abs(site.Frame.Easting-expectedFrame.Easting)+Math.Abs(site.Frame.Northing-expectedFrame.Northing)>1e-6||site.Points.Count!=unrotated.Points.Count)throw new Exception("생성전 회전 중심/Frame 불일치");
    for(var i=0;i<site.Points.Count;i++)if(site.Frame.ToSurvey(site.Points[i]).DistanceXY(unrotated.Frame.ToSurvey(unrotated.Points[i]))>1e-6)throw new Exception("회전 지형과 저장 시추 좌표 불일치");
    Console.WriteLine(string.Join("\n",site.Log));Console.WriteLine("PASS 실제 대규모 SHP 전체 점 유지/생성전 중심회전/공통 Frame 입력");return;
}
SiteChecks.Soil();
SiteChecks.SiteRotation();
SiteChecks.Rotation();
void Assert(bool yes,string name) {if(!yes) throw new Exception(name); checks++;Console.WriteLine("PASS "+name);}
void Reject(Action f,string name) {try {f();} catch(InvalidDataException) {Assert(true,name);return;}throw new Exception("Expected rejection: "+name);}
var frame=new SiteFrame("EPSG:5186",206600,550200,10,30); var p=new Point3(206637.937,550198.82,13.58); var q=frame.ToSurvey(frame.ToLocal(p));
Assert(p.DistanceXY(q)<1e-8 && Math.Abs(p.Z-q.Z)<1e-8,"좌표 왕복/회전/표고");
var data=new BoreholeSet {Crs="TEST",Reviewed=true,Boreholes=[new("BH-1",0,0,10,[new("토사",2),new("풍화암",6),new("연암",20)])]};
var split=Geology.Split("CIP-1",new(0,0),9,-5,.6,data,"TEST",GeologyMethod.Nearest,100);
Assert(split.Count==3,"지층 3구간 분할");
Assert(Math.Abs(split.Sum(x=>x.Length)-14)<1e-10,"분할 총길이 보존");
Assert(Math.Abs(split.Sum(x=>x.Volume)-Math.PI*.09*14)<1e-10,"분할 체적 보존");
Assert(split[0].TopElevation==9 && split[^1].BottomElevation==-5,"말뚝 상하단 clipping");
Reject(()=>Geology.Split("x",new(0,0),11,-5,.6,data,"TEST",GeologyMethod.Nearest,100),"지표 위 외삽 거부");
Reject(()=>Geology.Split("x",new(0,0),9,-15,.6,data,"TEST",GeologyMethod.Nearest,100),"조사심도 아래 외삽 거부");
Reject(()=>Geology.Split("x",new(200,0),9,-5,.6,data,"TEST",GeologyMethod.Nearest,100),"영향반경 밖 거부");
Reject(()=>data.Validate("OTHER"),"좌표계 불일치 거부");
Reject(()=>new Borehole("bad",0,0,0,[new("a",2),new("b",1)]).Validate(),"역전층 거부");
Reject(()=>new Borehole("bad",0,0,0,[new("a",double.NaN)]).Validate(),"NaN 거부");
Reject(()=>new Borehole("bad",0,0,0,[new("검토필요",2)]).Validate(),"미확인 지층명 거부");
data.Boreholes.Add(new("BH-2",10,0,12,[new("토사",2),new("풍화암",6),new("연암",20)]));
data.Boreholes.Add(new("BH-3",0,10,14,[new("토사",2),new("풍화암",6),new("연암",20)]));
var idw=Geology.Split("x",new(2,2),9,-5,.6,data,"TEST",GeologyMethod.Idw,100);
Assert(Math.Abs(idw.Sum(x=>x.Length)-14)<1e-10,"IDW 표고 보간/길이 보존");
Reject(()=>Geology.Split("x",new(20,20),9,-5,.6,data,"TEST",GeologyMethod.Idw,100),"convex hull 밖 보간 거부");
data.Boreholes[1]=data.Boreholes[1] with {Layers=[new("다른층",2),new("풍화암",6),new("연암",20)]};
Reject(()=>Geology.Split("x",new(2,2),9,-5,.6,data,"TEST",GeologyMethod.Idw,100),"층서 대응 불일치 보간 거부");
if(args.Length>0)
{
    var candidate=BoreholePdf.ExtractCandidates(args[0],"GRS80_TM_CENTRAL_200000_600000");
    Directory.CreateDirectory("local-data");File.WriteAllText("local-data/report.candidates.json",JsonSerializer.Serialize(candidate,BoreholeSet.JsonOptions));
    Assert(candidate.Boreholes.Count==5,"report.pdf BH 5공 추출");
    Assert(candidate.Boreholes.All(b=>b.Layers.Count>=5),"다중 페이지 지층 병합");
    Assert(!candidate.Reviewed,"PDF 후보 자동 확정 방지");
    Assert(candidate.ReviewNotes.Any(x=>x.Contains("층후")),"원본 층후 이상치 검출");
    Assert(candidate.Boreholes[0].Layers.Select(l=>l.BottomDepth).SequenceEqual(new[]{2,4.9,12,18.3,35}),"BH-1 심도 원문 대조");
    Assert(Math.Abs(candidate.Boreholes[1].Northing-550198.82)<1e-8 && candidate.Boreholes[1].GroundElevation==13.58,"BH-2 좌표/지표 EL 원문 대조");
    Assert(candidate.Boreholes[3].Layers[^1].BottomDepth==35 && candidate.ReviewNotes.Count(x=>x.Contains("층후"))==3,"BH-4 원본 층후 이상 3개 검출");
}
if(args.Length>1)
{
    SiteChecks.Run(args[1]);
    var files=Directory.GetFiles(args[1],"*F001*.shp",SearchOption.AllDirectories);
    Assert(files.Length==4,"실제 4도엽 등고선 입력");
    foreach(var file in files)
    {
        var shp=ShapeDataset.Load(file);
        Console.WriteLine("FIELDS "+string.Join(",",shp.Features[0].Attributes.Keys));
        var points=shp.TerrainPoints("등고수치",2);
        Assert(points.Count>3 && points.All(p=>p.Z>-100 && p.Z<1000),"실 SHP 표고 읽기 "+Path.GetFileName(Path.GetDirectoryName(file)));
        Console.WriteLine($"  records={shp.Features.Count} points={points.Count} z={points.Min(p=>p.Z)}..{points.Max(p=>p.Z)}");
    }
}
var recognition=DrawingDepth.Recognize(new[]{new DrawingText("section",new(),"CIP-1 TOP EL+13.25 L=20m"),new DrawingText("section",new(),"Ø530(연암상단까지)"),new DrawingText("section",new(),"E.L -5.30")});
var rockData=new BoreholeSet {Crs="TEST",Reviewed=true,Boreholes=[new("BH-1",0,0,13.25,[new("풍화토층",12),new("풍화암층",18.3),new("연암층",35)])]};
Assert(Math.Abs(Geology.SoftRockTop(new(1,1),rockData,"TEST",10)-(-5.05))<1e-9,"연암 상단 EL = 지표 EL - 직전 경계 심도");
Reject(()=>Geology.SoftRockTop(new(100,100),rockData,"TEST",10),"연암 선단 규칙 영향반경 거부");
Assert(recognition[0].BottomElevation==-6.75,"단면 TOP EL와 길이로 하단 계산");
Assert(recognition[1].BottomAtSoftRockTop && recognition[1].Diameter==.53,"연암 상단 선단 규칙/직경 인식");
Assert(recognition[2].ObservedElevation==-5.3 && recognition[2].TopElevation==null,"미지정 EL 자동 상단 오인 방지");
if(args.Length>2)
{
    var texts=DrawingDepth.ReadExtract(args[2]);var candidates=DrawingDepth.Recognize(texts);
    Assert(texts.Count>1000,"실 DWG 문자 추출");
    Assert(candidates.Any(x=>x.BottomAtSoftRockTop&&x.Diameter==.53),"실 DWG Ø530 연암상단까지 인식");
    Assert(candidates.Any(x=>x.ObservedElevation==-6.86),"실 DWG 음수 EL 인식");
    Console.WriteLine($"DWG texts={texts.Count} depth candidates={candidates.Count}");
}
Console.WriteLine($"{checks} checks passed.");

