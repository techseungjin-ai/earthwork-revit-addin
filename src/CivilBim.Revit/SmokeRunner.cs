using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using CivilBim.Core;

namespace CivilBim.Revit;

// 명시적으로 CIVILBIM_SMOKE_CONFIG 환경변수를 지정한 별도 Revit 실행에서만 동작한다.
// 사용자 문서를 수정하지 않고 새 프로젝트/검증용 패밀리를 생성한다.
public static class SmokeRunner
{
    public sealed class Config
    {
        public string OutputFolder {get;set;}="";
        public string SampleFolder {get;set;}="";
        public string Pdf {get;set;}="";
        public string DrawingExtract {get;set;}="";
    }
    public static void Schedule(UIControlledApplication app)
    {
        var path=Environment.GetEnvironmentVariable("CIVILBIM_SMOKE_CONFIG");if(string.IsNullOrWhiteSpace(path))return;
        EventHandler<IdlingEventArgs>? handler=null;
        handler=(sender,_)=>
        {
            app.Idling-=handler;
            if(sender is UIApplication ui)Run(ui,path);
        };app.Idling+=handler;
    }
    private static void Run(UIApplication ui,string configPath)
    {
        var config=JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath),BoreholeSet.JsonOptions)??throw new InvalidDataException("검증 설정 오류.");
        Directory.CreateDirectory(config.OutputFolder);var report=Path.Combine(config.OutputFolder,"revit-smoke.json");
        var results=new List<string>();Document? doc=null;
        File.WriteAllText(report,JsonSerializer.Serialize(new {Status="running",Results=results}));
        try
        {
            var application=ui.Application;
            var familyPath=Path.Combine(config.OutputFolder,"CivilBIM_TestCIP.rfa");
            using(var family=application.NewFamilyDocument(@"C:\ProgramData\Autodesk\RVT 2027\Family Templates\English\Metric Generic Model.rft"))
            {
                using(var t=new Transaction(family,"검증용 CIP 패밀리"))
                {
                    t.Start();family.FamilyManager.NewType("D530");
                    var length=family.FamilyManager.AddParameter("길이",GroupTypeId.Geometry,SpecTypeId.Length,true);
                    family.FamilyManager.Set(length,ModelBuilder.Feet(1));
                    var plane=SketchPlane.Create(family,Plane.CreateByNormalAndOrigin(XYZ.BasisZ,XYZ.Zero));
                    var profile=new CurveArray();var r=ModelBuilder.Feet(.265);
                    profile.Append(Arc.Create(XYZ.Zero,r,0,Math.PI,XYZ.BasisX,XYZ.BasisY));profile.Append(Arc.Create(XYZ.Zero,r,Math.PI,2*Math.PI,XYZ.BasisX,XYZ.BasisY));
                    var profiles=new CurveArrArray();profiles.Append(profile);
                    var extrusion=family.FamilyCreate.NewExtrusion(true,profiles,plane,ModelBuilder.Feet(1));
                    family.FamilyManager.AssociateElementParameterToFamilyParameter(extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM),length);
                    t.Commit();
                }
                family.SaveAs(familyPath,new SaveAsOptions {OverwriteExistingFile=true});
            }
            results.Add("generated test CIP family");
            doc=application.NewProjectDocument(@"C:\ProgramData\Autodesk\RVT 2027\Templates\Default_M_KOR.rte");
            var s=new Settings {Frame=new("EPSG:5186",206600,550200),CipDiameterParameter="",Diameter=.53};
            using(var t=new Transaction(doc,"CivilBIM 통합 검증"))
            {
                t.Start();if(!doc.LoadFamily(familyPath,out var loaded))throw new InvalidDataException("검증 패밀리 로드 실패.");
                var symbol=(FamilySymbol)doc.GetElement(loaded.GetFamilySymbolIds().First());s.CipFamily=symbol.UniqueId;
                var boreholes=BoreholePdf.ExtractCandidates(config.Pdf,s.Frame.Crs);boreholes.Reviewed=true; // 자동 테스트 fixture에서만 원문 대조된 입력을 확정
                var b=boreholes.Boreholes[0];var pos=new Point3(b.Easting,b.Northing);var center=ModelBuilder.Xyz(s.Frame.ToLocal(pos));
                var split=Geology.Split("TEST-CIP",pos,10,-15,.53,boreholes,s.Frame.Crs,GeologyMethod.Nearest,100);
                var instances=split.Select(segment=>ModelBuilder.Pile(doc,center,segment,s,"TEST","SMOKE")).ToList();
                doc.Regenerate();
                foreach(var instance in instances)ModelBuilder.VerifyUnchanged(instance,Storage.Info(instance)!);
                var measured=instances.Sum(i=>Storage.Info(i)!.Quantity);var expected=Math.PI*.53*.53/4*25;
                results.AddRange(instances.Select(i=>{var info=Storage.Info(i)!;return $"segment {info.Layer}: length={info.Length:F6}, volume={info.Quantity:F9}, analytic={Math.PI*.53*.53/4*info.Length:F9}";}));
                var whole=ModelBuilder.Pile(doc,center+new XYZ(ModelBuilder.Feet(2),0,0),new("REFERENCE","미분할",10,-15,.53,""),s,"TEST","SMOKE");
                var wholeVolume=Storage.Info(whole)!.Quantity;
                if(Math.Abs(measured-wholeVolume)>1e-6)throw new InvalidDataException($"네이티브 분할 전후 체적 보존 실패. split={measured:F9}, whole={wholeVolume:F9}");
                if(Math.Abs(measured-expected)/expected>.0001)throw new InvalidDataException("원통 이론체적과 네이티브 체적의 상대차 0.01% 초과.");
                results.Add($"native split/whole preservation: {measured:F9}/{wholeVolume:F9}, analytic relative difference={Math.Abs(measured-expected)/expected:G}");
                results.Add($"native CIP instances={instances.Count}, volume={measured}");
                var shapefiles=Directory.GetFiles(config.SampleFolder,"*F001*.shp",SearchOption.AllDirectories);
                var points=shapefiles.SelectMany(file=>ShapeDataset.Load(file).TerrainPoints("등고수치",2)).DistinctBy(p=>(Math.Round(p.X,3),Math.Round(p.Y,3))).Select(p=>ModelBuilder.Xyz(s.Frame.ToLocal(p))).ToList();
                var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First();
                var type=new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).Cast<ToposolidType>().First();
                var topo=Toposolid.Create(doc,points,type.Id,level.Id);doc.Regenerate();
                results.Add($"real SHP Toposolid={topo.Id.Value}, points={points.Count}");
                var dwg=Path.Combine(config.SampleFolder,"흙막이 도면.dwg");
                if(File.Exists(dwg))
                {
                    var plan=new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(v=>!v.IsTemplate);
                    if(!doc.Import(dwg,new DWGImportOptions {Unit=ImportUnit.Millimeter,ThisViewOnly=false,Placement=ImportPlacement.Origin},plan,out var cadId))throw new InvalidDataException("샘플 DWG Revit 가져오기 실패.");
                    var reader=CadReader.Read(doc,(ImportInstance)doc.GetElement(cadId));
                    var count=reader.Circles.Count(c=>c.Layer=="_CIP");
                    var raw=File.ReadLines(config.DrawingExtract).Where(l=>l.StartsWith("CIRCLE|_CIP|")).Select(l=>l.Split('|',5)).ToList();
                    if(count!=raw.Count)throw new InvalidDataException($"Revit/DWG CIP 원 수 불일치: {count}/{raw.Count}.");
                    var expectedCenters=raw.Select(c=>(X:Math.Round(Guard.Number(c[2])*.001,4),Y:Math.Round(Guard.Number(c[3])*.001,4))).ToHashSet();
                    if(reader.Circles.Where(c=>c.Layer=="_CIP").Any(c=>!expectedCenters.Contains((Math.Round(c.Center.X*.3048,4),Math.Round(c.Center.Y*.3048,4)))))
                        throw new InvalidDataException("Revit/DWG CIP 중심 좌표 정합 실패.");
                    results.Add($"real DWG Revit geometry: CIP circles={count}, paths={reader.Paths.Count}, layers={reader.Layers.Length}");
                }
                var viewType=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v=>v.ViewFamily==ViewFamily.ThreeDimensional);
                var threeD=View3D.CreateIsometric(doc,viewType.Id);threeD.Name="CivilBIM 검증 3D";
                Storage.Write(doc.ProjectInformation,s);t.Commit();
            }
            var file=Path.Combine(config.OutputFolder,"CivilBIM-Smoke.rvt");doc.SaveAs(file,new SaveAsOptions {OverwriteExistingFile=true});doc.Close(false);doc=null;
            File.WriteAllText(report,JsonSerializer.Serialize(new {Status="passed",Results=results,Model=file},BoreholeSet.JsonOptions));
        }
        catch(Exception ex)
        {
            if(doc!=null)try {doc.Close(false);}catch { }
            File.WriteAllText(report,JsonSerializer.Serialize(new {Status="failed",Results=results,Error=ex.ToString()},BoreholeSet.JsonOptions));
        }
    }
}
