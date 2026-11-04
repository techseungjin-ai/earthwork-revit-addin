using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace CivilBim.Revit;

public sealed class RevisionStartup : IExternalApplication
{
    public Result OnShutdown(UIControlledApplication a)=>Result.Succeeded;
    public Result OnStartup(UIControlledApplication a)
    {
        var file=Environment.GetEnvironmentVariable("CIVILBIM_REVISION_CONFIG");if(string.IsNullOrEmpty(file))return Result.Succeeded;
        var c=JsonSerializer.Deserialize<RevisionRunner.Config>(File.ReadAllText(file))!;
        a.ControlledApplication.FailuresProcessing+=(_,args)=>
        {
            var fa=args.GetFailuresAccessor();var failures=fa.GetFailureMessages();
            File.AppendAllText(Path.Combine(c.Output,"failures.log"),string.Join(Environment.NewLine,failures.Select(f=>f.GetSeverity()+": "+f.GetDescriptionText()))+Environment.NewLine);
            foreach(var f in failures.Where(f=>f.GetSeverity()==FailureSeverity.Warning))fa.DeleteWarning(f);
            if(failures.Any(f=>f.GetSeverity()!=FailureSeverity.Warning)){fa.SetFailureHandlingOptions(fa.GetFailureHandlingOptions().SetClearAfterRollback(true));args.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack);}
        };
        a.DialogBoxShowing+=(_,args)=>{File.AppendAllText(Path.Combine(c.Output,"dialogs.log"),args.DialogId+": "+(args is TaskDialogShowingEventArgs td?td.Message:"")+Environment.NewLine);if(args.DialogId=="Dialog_Revit_DocWarnDialog")args.OverrideResult(1);};
        EventHandler<IdlingEventArgs>? handler=null;handler=(sender,_)=>{a.Idling-=handler;if(sender is UIApplication ui)RevisionRunner.Run(ui,file);};a.Idling+=handler;return Result.Succeeded;
    }
}
public static class RevisionRunner
{
    public sealed class Config {public string Folder {get;set;}="";public string Output {get;set;}="";public string Pdf {get;set;}="";public double RotationDegrees {get;set;}}
    public static void Run(UIApplication ui,string configFile)
    {
        var c=JsonSerializer.Deserialize<Config>(File.ReadAllText(configFile))!;Directory.CreateDirectory(c.Output);var report=Path.Combine(c.Output,"result.json");var log=new List<string>();Document? doc=null;
        File.WriteAllText(report,"{\"Status\":\"running\"}");
        try
        {
            doc=ui.Application.NewProjectDocument(@"C:\ProgramData\Autodesk\RVT 2027\Templates\Default_M_KOR.rte");
            Settings settings;
            using(var t=new Transaction(doc,"검토 유형 / 재질"))
            {
                t.Start();var type=new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).Cast<ToposolidType>().First();
                var road=(ToposolidType)type.Duplicate("검토_도로");var materialId=Material.Create(doc,"검토_주변건물");((Material)doc.GetElement(materialId)).Color=new(181,190,202);
                var asphalt=Material.Create(doc,"검토_도로포장");((Material)doc.GetElement(asphalt)).Color=new(66,74,85);
                var compound=road.GetCompoundStructure();foreach(var (layer,index) in compound.GetLayers().Select((x,i)=>(x,i)))compound.SetMaterialId(index,asphalt);road.SetCompoundStructure(compound);
                settings=new Settings {SiteFolder=c.Folder,TerrainType=type.UniqueId,RoadType=road.UniqueId,BuildingMaterial=doc.GetElement(materialId).UniqueId,Scale=1000,Sampling=2,Frame=new("",0,0,0,c.RotationDegrees)};t.Commit();
            }
            var context=ContextBuilder.Generate(doc,settings);log.AddRange(context.Log);
            if(Storage.Load(doc).Frame.RotationDegrees!=c.RotationDegrees||Storage.Info(context.Terrain)?.Frame!=settings.Frame)throw new InvalidDataException("대지 회전 원점/각도 저장 실패.");
            var projectPosition=doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);if(Math.Abs(projectPosition.Angle)+Math.Abs(projectPosition.EastWest)+Math.Abs(projectPosition.NorthSouth)+Math.Abs(projectPosition.Elevation)>1e-7)throw new InvalidDataException("공유좌표를 변경했습니다.");
            log.Add($"회전 {c.RotationDegrees}°의 프로젝트/지형 공통 Frame 저장 확인. 공유좌표 변경 없음.");
            Export(doc,context.View,Path.Combine(c.Output,"01-대지-도로-주변건물"));
            ExportWindow(new ContextWindow(doc,settings),Path.Combine(c.Output,"00-SHP-폴더-선택화면.png"));
            if(context.Roads.Count==0 || context.Buildings.Count==0)throw new InvalidDataException("실제 도로/건물이 생성되지 않았습니다.");
            var roadArea=context.Roads.Sum(id=>ContextBuilder.Surface((Toposolid)doc.GetElement(id)).Triangles.Sum(t=>t.Area));
            log.Add($"생성된 Revit 도로 구획의 실제 수평 투영면적 합계 {roadArea:F4}m².");
            var borings=Core.BoreholePdf.ExtractCandidates(c.Pdf,settings.Frame.Crs);borings.Reviewed=true;
            var sitePoints=borings.Boreholes.Select(b=>settings.Frame.ToLocal(new Core.Point3(b.Easting,b.Northing))).ToList();
            var site=Core.Polygons.Factory.CreateMultiPointFromCoords(sitePoints.Select(p=>new NetTopologySuite.Geometries.Coordinate(p.X,p.Y)).ToArray()).ConvexHull().Buffer(3);
            var soils=SoilBuilder.Generate(doc,context.Terrain,site,borings,settings,-10,100);
            log.Add($"예시 작업경계(시추공 범위+3m): 지층 {soils.Elements.Count}개; 굴착 {soils.Bodies.Sum(b=>b.ExcavatedVolume):F6}m³; 실제 확정 대지 경계 아님.");
            Export(doc,soils.View,Path.Combine(c.Output,"02-예시작업대지-지층"));
            ViewSheet quantities;
            using(var t=new Transaction(doc,"물량표 검토 시트")){t.Start();quantities=ViewSheet.Create(doc,ElementId.InvalidElementId);quantities.Name="지층별 굴착 물량";quantities.SheetNumber="CBIM-QTY";ScheduleSheetInstance.Create(doc,quantities.Id,soils.Schedule.Id,XYZ.Zero);t.Commit();}
            Export(doc,quantities,Path.Combine(c.Output,"03-지층별굴착물량"));
            PlacementChecks.Run(ui,doc,settings,c.Output,log);
            PlacementChecks.CipSplit(ui,doc,settings,borings,c.Output,log);
            var file=Path.Combine(c.Output,"CivilBIM-대지검토.rvt");doc.SaveAs(file,new SaveAsOptions {OverwriteExistingFile=true});
            Export(doc,context.View,Path.Combine(c.Output,"01-대지-도로-주변건물"));
            doc.Close(false);doc=null;
            File.WriteAllText(report,JsonSerializer.Serialize(new {Status="passed",Log=log,Model=file},Core.BoreholeSet.JsonOptions));
        }
        catch(Exception ex){if(doc!=null)try{doc.Close(false);}catch{}File.WriteAllText(report,JsonSerializer.Serialize(new {Status="failed",Log=log,Error=ex.ToString()},Core.BoreholeSet.JsonOptions));}
    }
    public static void Export(Document doc,View view,string file)
    {
        var options=new ImageExportOptions {ExportRange=ExportRange.SetOfViews,FilePath=file,HLRandWFViewsFileType=ImageFileType.PNG,ShadowViewsFileType=ImageFileType.PNG,ZoomType=ZoomFitType.FitToPage,PixelSize=1800,ImageResolution=ImageResolution.DPI_150};
        options.SetViewsAndSheets(new[]{view.Id});doc.ExportImage(options);
    }
    private static void ExportWindow(System.Windows.Window window,string file)
    {
        var content=(System.Windows.FrameworkElement)window.Content;window.Content=null;
        var border=new System.Windows.Controls.Border {Background=System.Windows.Media.Brushes.White,Child=content,Width=690,Height=710};
        border.Measure(new(690,710));border.Arrange(new(0,0,690,710));border.UpdateLayout();
        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(690,710,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(border);
        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using var stream=File.Create(file);encoder.Save(stream);window.Close();
    }
}
