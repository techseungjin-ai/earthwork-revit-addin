using CivilBim.Core;
using NetTopologySuite.Geometries;

public static class SiteChecks
{
    public static void SiteRotation()
    {
        const double east=200000,north=500000;
        var holes=new BoreholeSet {Crs="TEST",Reviewed=true,Boreholes=[new("BH1",east,north+5,10,[new("매립층",2),new("퇴적층",20)]),new("BH2",east+10,north+5,10,[new("매립층",4),new("퇴적층",20)])]};
        foreach(var angle in new[]{-90.0,0,37,90,180})
        {
            var frame=new SiteFrame("TEST",east,north,3,angle);
            Point3 P(double x,double y,double z=10)=>frame.ToLocal(new(east+x,north+y,z));
            var mesh=new TerrainMesh(new[]{new TerrainTriangle(P(0,0),P(10,0),P(10,10)),new TerrainTriangle(P(0,0),P(10,10),P(0,10))});
            var boundary=Polygons.Factory.CreatePolygon(new[]{P(0,0),P(10,0),P(10,10),P(0,10),P(0,0)}.Select(p=>new Coordinate(p.X,p.Y)).ToArray());
            var bodies=SiteGeology.Build(mesh,boundary,holes,frame,5,100);
            var compact=SiteGeology.ResolveScope(mesh,SiteGeology.BoreholeScope(holes,frame));
            if(compact.Area>=99||compact.Area<=0)throw new Exception("시추공 주변 지층이 대지 전체로 확대됨");
            var compactBodies=SiteGeology.Build(mesh,compact,holes,frame,0,1,false,false);
            if(Math.Abs(compactBodies.Sum(b=>b.Volume)-compact.Area*20)>1e-6)throw new Exception("시추 주변 범위 지층 체적/회전 불일치");
            if(Math.Abs(SiteGeology.ResolveScope(mesh).Area-100)>1e-6)throw new Exception("경계 입력 없이 선택 지형 전체 범위 적용 실패");
            var subset=Polygons.Factory.CreatePolygon(new[]{P(0,0),P(5,0),P(5,10),P(0,10),P(0,0)}.Select(p=>new Coordinate(p.X,p.Y)).ToArray());
            if(Math.Abs(SiteGeology.ResolveScope(mesh,subset).Area-50)>1e-6)throw new Exception("작업대지 선택 범위 클리핑 실패");
            var geologyOnly=SiteGeology.Build(mesh,boundary,holes,frame,-10000,100,false);
            if(Math.Abs(geologyOnly.Sum(b=>b.Volume)-bodies.Sum(b=>b.Volume))>1e-6||geologyOnly.Any(b=>b.Excavation.Count!=0))throw new Exception("굴착 EL 없이 지층 생성/전체체적 보존 실패");
            var noRadiusInput=SiteGeology.Build(mesh,boundary,holes,frame,0,1,false,false);
            if(Math.Abs(noRadiusInput.Sum(b=>b.Volume)-bodies.Sum(b=>b.Volume))>1e-6)throw new Exception("선택 대지 범위에서 반경 입력 없이 지층 생성 실패");
            if(Math.Abs(bodies.Sum(b=>b.Volume)-2000)>1e-6||Math.Abs(bodies.Sum(b=>b.ExcavatedVolume)-500)>1e-6||Math.Abs(bodies.Single(b=>b.Layer=="매립층").ExcavatedVolume-300)>1e-6)throw new Exception("회전한 지형/시추 지층/굴착체적 정합 실패: "+angle);
            var boring=frame.ToLocal(new(east,north+5,10));var survey=frame.ToSurvey(boring);
            var segments=Geology.Split("rotated-CIP",survey,9,5,.6,holes,"TEST",GeologyMethod.Nearest,100);
            if(segments.Any(s=>s.BoreholeIds!="BH1")||segments.Count!=2||segments[0].BottomElevation!=8)throw new Exception("회전한 CIP 역변환/최근접 공/지층 경계 정합 실패.");
            var reference=frame.ToLocal(new(east+10,north,10));var radians=angle*Math.PI/180;
            if(reference.DistanceXY(new(10*Math.Cos(radians),10*Math.Sin(radians)))>1e-8||survey.DistanceXY(new(east,north+5))>1e-8)throw new Exception("회전 부호/시추좌표 왕복 실패.");
        }
        Console.WriteLine("PASS 대지 -90/0/37/90/180도: 시추 위치·최근접 CIP·지층 경계·전체/굴착 체적 불변");
    }
    public static void Rotation()
    {
        foreach(var angle in new[]{0,Math.PI/4,Math.PI/2})
        {
            Point3 R(double x,double y)=>new(x*Math.Cos(angle)-y*Math.Sin(angle),x*Math.Sin(angle)+y*Math.Cos(angle));
            var h=new[]{new PlanSegment(R(-.1,-.15),R(.1,-.15)),new PlanSegment(R(-.1,.15),R(.1,.15)),new PlanSegment(R(0,-.15),R(0,.15))};
            var symbols=PlanRecognition.HPiles(h);if(symbols.Count!=1 || symbols[0].Center.DistanceXY(new())>1e-9 || Math.Abs(Math.Sin(symbols[0].Angle-angle))>1e-8)throw new Exception("H-PILE 회전 인식 실패.");
        }
        var top=new[]{new PlanSegment(new(0,0,10),new(10,0,12))};var bottom=new[]{new PlanSegment(new(0,0,-10),new(10,0,-15))};
        if(ElevationMatching.At(new(5,0),top,.1)!=11 || ElevationMatching.At(new(5,0),bottom,.1)!=-12.5)throw new Exception("전개도 3D 곡선 심도 대응 실패.");
        Console.WriteLine("PASS H-PILE 0/45/90도 회전 및 3D 상하단 곡선 대응");
        var rectangle=new[]{new Point3(0,0),new Point3(.3,0),new Point3(.3,.2),new Point3(0,.2),new Point3(0,0)};
        if(PlanRecognition.HPiles(rectangle.Zip(rectangle.Skip(1),(a,b)=>new PlanSegment(a,b))).Count!=0)throw new Exception("단순 사각형을 H-PILE로 오인식했습니다.");
        var outline=new PlanPath([new(0,-.15),new(10,-.15),new(10,.15),new(0,.15),new(0,-.15)],true);
        var centerline=HorizontalRecognition.Centerlines([outline]);if(centerline.Count!=1 || centerline[0].Length!=10 || centerline[0].A.Y!=0)throw new Exception("STRUT 외곽 중심선 변환 실패.");
        var tierLines=new[]{0,6,12}.SelectMany(y=>new[]{new PlanSegment(new(0,y-.3),new(10,y-.3)),new PlanSegment(new(0,y+.3),new(10,y+.3))});
        var tiers=HorizontalRecognition.DrawingTiers(tierLines,0,10,.5);if(!tiers.SequenceEqual(new[]{16.0,13.0,10.0}))throw new Exception("전개도 배율/윤곽 병합 단수 인식 실패.");
        Console.WriteLine("PASS STRUT 외곽 중심선 및 세로배율 보정 3단 인식");
        var alignment=new[]{new Point3(0,0),new Point3(10,0),new Point3(10,10)};
        var station=DevelopmentMapping.Station(new(10,5.0),alignment);
        var depth=DevelopmentMapping.Elevation(station,[new(new(100,20),new(140,10))],100,2,20,10,.5);
        if(Math.Abs(station-15)>1e-8||Math.Abs(depth-6.25)>1e-8)throw new Exception("2D 전개도 거리/EL 축 보정 실패.");
        Console.WriteLine("PASS 꺾인 평면 경로의 거리 → 2D 전개도 X / EL 보정");
    }
    public static void Soil()
    {
        var chain=ConnectedPath.Join(new IReadOnlyList<Point3>[]{new[]{new Point3(10,10),new Point3(10,0)},new[]{new Point3(0,0),new Point3(10,0)}});
        if(chain.Count!=3||chain.Zip(chain.Skip(1),(a,b)=>a.DistanceXY(b)).Sum()!=20)throw new Exception("순서/방향이 다른 Revit 선 연결 실패");
        var offset=ConnectedPath.Nearest(new Point3(12,5),chain);if(offset.Distance!=2)throw new Exception("0.5m 밖 부재 최근접 경로 대응 실패");
        var retraced=ConnectedPath.Cad(new[]{new Point3(0,0),new Point3(5,0),new Point3(5,10),new Point3(6,10),new Point3(5,10),new Point3(5,0),new Point3(0,.0008)});if(retraced.Count!=4||Math.Abs(retraced.Zip(retraced.Skip(1),(a,b)=>a.DistanceXY(b)).Sum()-16)>.001)throw new Exception("CAD 되짚기 중복 경로 단일화 실패");
        var loop=ConnectedPath.Join(new IReadOnlyList<Point3>[]{new[]{new Point3(0,0),new Point3(10,0)},new[]{new Point3(10,10),new Point3(0,0)},new[]{new Point3(10,0),new Point3(10,10)}});if(loop[0]!=loop[^1]||loop.Count!=4)throw new Exception("폐합 경로 연결 실패");
        var snapped=ConnectedPath.Join(new IReadOnlyList<Point3>[]{new[]{new Point3(0,0),new Point3(1,0)},new[]{new Point3(1.0005,0),new Point3(2,0)}});if(snapped.Count!=3)throw new Exception("1mm 이내 끝점 연결 실패");
        void BadPath(IReadOnlyList<Point3>[] input){try{ConnectedPath.Join(input);}catch(InvalidDataException){return;}throw new Exception("잘못된 경로 허용");}
        BadPath([new[]{new Point3(0,0),new Point3(1,0)},new[]{new Point3(3,0),new Point3(4,0)}]);
        BadPath([new[]{new Point3(0,0),new Point3(1,0)},new[]{new Point3(1,0),new Point3(2,0)},new[]{new Point3(1,0),new Point3(1,1)}]);
        BadPath([new[]{new Point3(0,0),new Point3(1,0)},new[]{new Point3(1,0),new Point3(0,0)}]);
        Console.WriteLine("PASS Revit 다중 경로선 순서/반전/폐합/끝점연결, 단절/분기/중복 거부, 2m 이격 부재 대응");
        var pdfOnly=new BoreholeSet {Crs="PDF-LOCAL",Reviewed=true,Boreholes=[new("A",200000,500000,10,[new("매립토",2),new("암",4)]),new("B",200010,500000,20,[new("매립토",2),new("암",4)])]};
        var pdfFrame=SiteGeology.BoreholeFrame(pdfOnly);var pdfScope=SiteGeology.BoreholeScope(pdfOnly,pdfFrame);
        var pdfBodies=SiteGeology.BuildFromBoreholes(pdfOnly,pdfFrame);
        if(Math.Abs(pdfBodies.Sum(b=>b.Volume)-pdfScope.Area*4)>1e-6)throw new Exception($"지형 없이 PDF 지표/심도만으로 지층체적 생성 실패: {pdfBodies.Sum(b=>b.Volume):R} / {pdfScope.Area*4:R} / "+string.Join(";",pdfBodies.Select(b=>$"{b.Layer}:{b.Volume:R}")));
        var fill=pdfBodies.Single(b=>b.Layer=="매립토");
        if(fill.Wedges.Any(w=>Math.Abs(w.A.Z-w.Bottom-2)>1e-8||Math.Abs(w.B.Z-w.Bottom-2)>1e-8||Math.Abs(w.C.Z-w.Bottom-2)>1e-8))throw new Exception("각 공 PDF 지표EL·층후 보존 실패");
        if(pdfFrame.ToSurvey(pdfFrame.ToLocal(new(200000,500000,10)))!=new Point3(200000,500000,10))throw new Exception("독립 시추좌표 왕복 실패");
        var savedFrame=new SiteFrame("PDF-LOCAL",199990,499990,3,37);if(SiteGeology.BoreholeFrame(pdfOnly,savedFrame)!=savedFrame)throw new Exception("기존 좌표변환 자동 사용 실패");
        if(SiteGeology.BoreholeFrame(pdfOnly,new SiteFrame("OTHER",0,0))!=pdfFrame)throw new Exception("다른좌표계 Frame 강제사용");
        foreach(var angle in new[]{-90,0,37,90,180})
        {
            var f=pdfFrame with {Elevation=3,RotationDegrees=angle};var b=SiteGeology.BuildFromBoreholes(pdfOnly,f);
            if(Math.Abs(b.Sum(x=>x.Volume)-pdfBodies.Sum(x=>x.Volume))>1e-6)throw new Exception("PDF 독립 지층 회전/표고원점 체적 불변 실패");
        }
        var single=new BoreholeSet {Crs="PDF-LOCAL",Reviewed=true,Boreholes=[pdfOnly.Boreholes[0]]};var oneFrame=SiteGeology.BoreholeFrame(single);
        if(Math.Abs(SiteGeology.BuildFromBoreholes(single,oneFrame).Sum(b=>b.Volume)-SiteGeology.BoreholeScope(single,oneFrame).Area*4)>1e-6)throw new Exception("한공 PDF 독립지층 생성 실패");
        Console.WriteLine("PASS 지형 없는 PDF 지층: 1공/2공·서로 다른 지표EL·층후·좌표왕복·회전체적 검증");
        if(SectionLineHeight.At(5,[new(new(0,10),new(10,20))])!=15)throw new Exception("단면 사선 높이 보간 실패");
        if(SectionLineHeight.At(5,[new(new(10,20),new(0,10))])!=15)throw new Exception("반대 방향 선 높이 실패");
        if(SectionLineHeight.At(15,[new(new(0,10),new(10,20))])!=null)throw new Exception("기준선 범위 밖 부재를 변경함");
        if(SectionLineHeight.At(5,[new(new(0,10),new(5,15)),new(new(5,15),new(10,20))])!=15)throw new Exception("연속선 경계 중복 대응 실패");
        try{SectionLineHeight.At(5,[new(new(0,10),new(10,10)),new(new(0,20),new(10,20))]);throw new Exception("복수 높이 거부 실패");}catch(InvalidDataException){}
        var flat=new TerrainMesh(new[]{new TerrainTriangle(new(0,0,8),new(10,0,8),new(0,10,8))});
        var equalTop=new BoreholeSet {Crs="TEST",Reviewed=true,Boreholes=[new("B",0,0,10,[new("윗층",2),new("아랫층",4)])]};
        var equality=SiteGeology.Build(flat,SiteGeology.ResolveScope(flat),equalTop,new SiteFrame("TEST",0,0,0,0),0,1,false,false);
        if(Math.Abs(equality.Sum(b=>b.Volume)-100)>1e-8)throw new Exception("지표가 지층 경계와 같을 때 중복 솔리드 생성");
        var mesh=new TerrainMesh(new[]{new TerrainTriangle(new(0,0,10),new(10,0,10),new(10,10,10)),new TerrainTriangle(new(0,0,10),new(10,10,10),new(0,10,10))});
        var site=Polygons.Factory.CreatePolygon(new[]{new Coordinate(0,0),new Coordinate(10,0),new Coordinate(10,10),new Coordinate(0,10),new Coordinate(0,0)});
        var holes=new BoreholeSet {Crs="TEST",Reviewed=true,Boreholes=[new("BH1",0,5,10,[new("매립층",2),new("퇴적층",20)]),new("BH2",10,5,10,[new("매립층",4),new("퇴적층",20)])]};
        var body=SiteGeology.Build(mesh,site,holes,new("TEST",0,0),5,100);
        if(Math.Abs(body.Sum(b=>b.ExcavatedVolume)-500)>1e-6 || Math.Abs(body.Single(b=>b.Layer=="매립층").ExcavatedVolume-300)>1e-6)throw new Exception("최근접 구역의 지층별 굴착 체적 검증 실패.");
        if(Math.Abs(body.Sum(b=>b.Volume)-2000)>1e-6)throw new Exception("지층 전체 체적 보존 실패.");
        var courtyard=Polygons.Factory.CreatePolygon(new[]{new Coordinate(2,2),new Coordinate(8,2),new Coordinate(8,8),new Coordinate(2,8),new Coordinate(2,2)});
        var withHole=SiteGeology.Build(mesh,site.Difference(courtyard),holes,new("TEST",0,0),5,100);
        if(Math.Abs(withHole.Sum(b=>b.ExcavatedVolume)-320)>1e-6)throw new Exception("작업대지 구멍 굴착 체적 검증 실패.");
        var slope=new TerrainMesh(new[]{new TerrainTriangle(new(0,0,10),new(10,0,12),new(10,10,12)),new TerrainTriangle(new(0,0,10),new(10,10,12),new(0,10,10))});
        var sloped=SiteGeology.Build(slope,site,holes,new("TEST",0,0),5,100);
        if(Math.Abs(sloped.Sum(b=>b.ExcavatedVolume)-600)>1e-6)throw new Exception("경사진 TIN 굴착 체적 검증 실패.");
        Console.WriteLine("PASS 작업대지 최근접 지층/전체 체적 보존/중정/경사진 TIN 굴착 체적");
    }
    public static void Run(string folder)
    {
        var d=SiteDataset.Load(folder,1000,2);Console.WriteLine(string.Join("\n",d.Log.Take(10)));
        if(d.Points.Count<100 || d.Roads.Area<=0 || d.Buildings.Count<10)throw new Exception("실제 SHP 폴더 지형/도로/건물 입력 실패.");
        var e=SiteDataset.Load(folder,5000,5);
        if(d.Frame!=e.Frame || Math.Abs(d.Roads.Area-e.Roads.Area)>.001 || d.Buildings.Count!=e.Buildings.Count)throw new Exception("축척을 바꿔 좌표/도로/건물 수가 달라졌습니다.");
        var rotated=SiteDataset.Load(folder,1000,2,rotationDegrees:37);
        if(rotated.Frame.Easting!=d.Frame.Easting||rotated.Frame.Northing!=d.Frame.Northing||rotated.Frame.RotationDegrees!=37||rotated.Points.Count!=d.Points.Count||Math.Abs(rotated.Roads.Area-d.Roads.Area)>.01||rotated.Buildings.Count!=d.Buildings.Count)throw new Exception($"실 SHP 회전 불변 실패: points {d.Points.Count}/{rotated.Points.Count}, roads {d.Roads.Area:F6}/{rotated.Roads.Area:F6}, buildings {d.Buildings.Count}/{rotated.Buildings.Count}, origin {d.Frame.Easting}/{rotated.Frame.Easting}.");
        var expected=rotated.Frame.ToLocal(d.Frame.ToSurvey(d.Points[0]));
        if(rotated.Points[0].DistanceXY(expected)>1e-6)throw new Exception("실 SHP 지형점 공통 회전 실패.");
        var center=rotated.Frame.ToLocal(d.Frame.ToSurvey(new(d.Roads.Centroid.X,d.Roads.Centroid.Y)));
        if(new Point3(rotated.Roads.Centroid.X,rotated.Roads.Centroid.Y).DistanceXY(center)>1e-5)throw new Exception("실 SHP 도로 공통 회전 실패.");
        Console.WriteLine("PASS 실 SHP 37도 회전: 지형점·도로중심·도로면적·건물수·자동원점 정합");
        var frame=new SiteFrame("TEST",0,0);
        var f=new ShapeFeature(1,5,[[new(0,0),new(10,0),new(10,10),new(0,10),new(0,0)],[new(2,2),new(8,2),new(8,8),new(2,8),new(2,2)]],[],false);
        var polygon=Polygons.FromFeature(f,frame);
        if(Math.Abs(polygon.Area-64)>1e-6)throw new Exception("건물 중정 구멍 처리가 잘못되었습니다.");
        Console.WriteLine($"PASS 폴더 입력/축척 독립/중정; roads={d.Roads.Area:F3}m² buildings={d.Buildings.Count} points={d.Points.Count}");
    }
}
