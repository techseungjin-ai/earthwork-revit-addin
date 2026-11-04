using System.Globalization;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CivilBim.Core;
using Microsoft.Win32;

namespace CivilBim.Revit;

[Transaction(TransactionMode.Manual)]
public sealed class WorkflowCommand:IExternalCommand
{
    private ImportInstance? cad;
    private CadReader? cadData;
    public Result Execute(ExternalCommandData commandData,ref string message,ElementSet elements)
    {
        var initial=commandData.Application.ActiveUIDocument;if(initial==null||initial.Document.IsFamilyDocument)return Result.Cancelled;var doc=initial.Document;var settings=Storage.Load(doc);
        if(!string.IsNullOrWhiteSpace(settings.CadUniqueId)&&doc.GetElement(settings.CadUniqueId) is ImportInstance existing)
        {cad=existing;cadData=CadReader.Read(doc,cad);if(settings.PlanMin is Point3 min&&settings.PlanMax is Point3 max)cadData.Crop(ModelBuilder.Xyz(min),ModelBuilder.Xyz(max));}
        ModelessHost.Open(commandData.Application,"통합도구",()=>new WorkflowWindow(settings,cadData?.Layers??[],FamilyBuilder.Choices(doc),doc:doc),window=>window.Submitted+=()=>window.Api(app=>
        {
            var ui=app.ActiveUIDocument;settings=window.Settings;
            if(window.CurrentCad is ImportInstance choice&&cad?.Id!=choice.Id){cad=choice;cadData=CadReader.Read(doc,choice);settings.CadUniqueId=choice.UniqueId;settings.PlanMin=null;settings.PlanMax=null;}
            if(window.Action=="PickCad")
            {
                var selected=ui.Selection.GetElementIds().Select(doc.GetElement).OfType<ImportInstance>().ToList();cad=selected.Count==1?selected[0]:(ImportInstance)doc.GetElement(ui.Selection.PickObject(ObjectType.Element,new CadFilter(),"CAD 링크 / 가져오기를 선택하세요."));
                cadData=CadReader.Read(doc,cad);settings.CadUniqueId=cad.UniqueId;settings.PlanMin=null;settings.PlanMax=null;settings.CoordinatesConfirmed=false;window.SetCad(cad);window.UpdateLayers(cadData.Layers);window.SetStatus($"CAD 레이어 {cadData.Layers.Length}개, 원 {cadData.Circles.Count}개 분석 완료.");return;
            }
            if(window.Action=="InspectShp"){Inspect(settings);return;}
            if(window.Action=="CropCad")
            {
                NeedCad();if(Math.Abs(ui.ActiveView.ViewDirection.Z)<.999)throw new InvalidDataException("평면 뷰에서 영역을 지정하세요.");var box=ui.Selection.PickBox(PickBoxStyle.Directional,"가시설 평면 범위를 지정하세요.");cadData=CadReader.Read(doc,cad!);cadData.Crop(box.Min,box.Max);settings.PlanMin=ModelBuilder.Meters(box.Min);settings.PlanMax=ModelBuilder.Meters(box.Max);window.SetStatus($"평면 원 {cadData.Circles.Count}개 / 경로 {cadData.Paths.Count}개.");return;
            }
            if(window.Action=="Depths")
            {
                var d=new OpenFileDialog {Filter="단면/전개도 DWG|*.dwg"};if(d.ShowDialog()!=true)return;var candidates=DrawingDepth.Recognize(DrawingDepth.ReadExtract(DwgExtractor.Extract(d.FileName)));if(candidates.Count==0)throw new InvalidDataException("EL/길이 후보가 없습니다.");
                ModelessHost.Open(app,"깊이후보:"+Guid.NewGuid(),()=>new DepthReviewWindow(candidates,settings.PileTop,settings.PileBottom),review=>review.Submitted+=()=>{window.SetDepth(review);review.Close();});return;
            }
            if(window.Action=="LoadFamily")
            {
                var d=new OpenFileDialog {Filter="Revit 패밀리|*.rfa"};if(d.ShowDialog()!=true)return;using var t=new Transaction(doc,"패밀리 로드");t.Start();if(!doc.LoadFamily(d.FileName,out _))throw new InvalidDataException("패밀리 로드 실패 또는 이미 로드된 패밀리입니다.");t.Commit();window.UpdateFamilies(FamilyBuilder.Choices(doc));return;
            }
            if(window.Action is "Pdf" or "Review")
            {
                BoreholeSet input;
                if(window.Action=="Pdf"){var d=new OpenFileDialog {Filter="시추 PDF|*.pdf"};if(d.ShowDialog()!=true)return;input=BoreholePdf.ExtractCandidates(d.FileName,settings.Frame.Crs);}
                else input=JsonSerializer.Deserialize<BoreholeSet>(File.ReadAllText(settings.BoreholeJson),BoreholeSet.JsonOptions)??throw new InvalidDataException("시추자료가 없습니다.");
                ModelessHost.Review(app,new(input),window.SetBorehole);return;
            }
            if(window.Action=="Export"){Export(doc);return;}
            if(window.Action=="PreviewCad"){NeedCad();window.SetStatus(Preview(settings));return;}
            settings.Frame.Validate();if(!window.CoordinateConfirmation)throw new InvalidDataException("좌표·단위 정합 확인란을 체크하세요.");var old=Storage.Load(doc);if(Storage.Generated(doc).Count>0&&old.Frame!=settings.Frame)throw new InvalidDataException("생성 모델이 있는 프로젝트에서 계산 좌표 기준은 바꿀 수 없습니다.");
            if(window.Action=="Save"){Mutate(doc,settings,"설정 저장",()=>{});return;}
            if(window.Action=="Terrain")Terrain(doc,settings);else if(window.Action=="Shoring"){NeedCad();Shoring(doc,settings);}else if(window.Action=="Split")Split(doc,settings,notify:false);else if(window.Action=="Excavate"){NeedCad();Excavate(doc,settings);}
        },true));return Result.Succeeded;
    }
    private void NeedCad(){if(cad==null || !cad.IsValidObject || cadData==null) throw new InvalidDataException("먼저 CAD를 선택하세요.");}
    private static void Mutate(Document doc,Settings settings,string name,Action action)
    {
        using var transaction=new Transaction(doc,name);transaction.Start();
        try {action();Storage.Write(doc.ProjectInformation,settings);if(transaction.Commit()!=TransactionStatus.Committed) throw new InvalidOperationException("Revit 트랜잭션이 커밋되지 않았습니다.");}
        catch {if(transaction.GetStatus()==TransactionStatus.Started)transaction.RollBack();throw;}
    }
    private static List<string> Shps(Settings s)
    {
        var files=s.Shp.Split('|',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Distinct().ToList();
        if(files.Count==0)throw new InvalidDataException("등고선/표고점 SHP를 선택하세요.");return files;
    }
    private static void Inspect(Settings s)
    {
        var text=new StringBuilder();
        foreach(var file in Shps(s))
        {
            var d=ShapeDataset.Load(file);var pts=d.Features.SelectMany(f=>f.Parts).SelectMany(p=>p).ToList();
            text.AppendLine(Path.GetFileName(file)+$": {d.Features.Count}레코드");text.AppendLine("필드: "+string.Join(", ",d.Features.SelectMany(f=>f.Attributes.Keys).Distinct()));
            if(pts.Count>0) text.AppendLine($"E {pts.Min(p=>p.X):F3}~{pts.Max(p=>p.X):F3}, N {pts.Min(p=>p.Y):F3}~{pts.Max(p=>p.Y):F3}");
            text.AppendLine(d.Projection);
        }
        TaskDialog.Show("SHP 검사",text.ToString());
    }
    private static bool Replace(Document doc,string source,params string[] kinds)
    {
        var existing=Storage.Generated(doc).Where(x=>x.Info.Source==source && kinds.Contains(x.Info.Kind)).ToList();if(existing.Count==0)return true;
        var d=new TaskDialog("CivilBIM 재생성") {MainInstruction=$"같은 원본의 CivilBIM 요소 {existing.Count}개가 있습니다.",MainContent="기존 요소를 교체하고 다시 생성합니다. 오류 시 변경 전체를 취소합니다.",CommonButtons=TaskDialogCommonButtons.Cancel};
        d.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,"기존 CivilBIM 요소 교체");return d.Show()==TaskDialogResult.CommandLink1;
    }
    private static void DeleteGenerated(Document doc,string source,params string[] kinds)
    {
        var ids=Storage.Generated(doc).Where(x=>x.Info.Source==source && kinds.Contains(x.Info.Kind)).Select(x=>x.Element.Id).ToList();if(ids.Count>0)doc.Delete(ids);
    }
    private static void Terrain(Document doc,Settings s)
    {
        var files=Shps(s);var datasets=files.Select(ShapeDataset.Load).ToList();
        if(Storage.Generated(doc).Any(x=>x.Info.Kind=="Excavation"))throw new InvalidDataException("굴착 바닥이 있는 지형은 재생성할 수 없습니다. CivilBIM 굴착 바닥을 먼저 삭제하세요.");
        if(datasets.Select(d=>d.Projection.Trim()).Distinct().Count()!=1)throw new InvalidDataException("여러 SHP의 PRJ가 다릅니다. 동일 좌표계로 변환하세요.");
        var points=datasets.SelectMany(d=>d.TerrainPoints(s.ElevationField,s.Sampling)).ToList();
        var unique=new Dictionary<(long,long),Point3>();
        foreach(var p in points)
        {
            var key=((long)Math.Round(p.X*1000),(long)Math.Round(p.Y*1000));
            if(unique.TryGetValue(key,out var old)&&Math.Abs(old.Z-p.Z)>.05)throw new InvalidDataException("도엽 간 같은 위치의 표고가 충돌합니다.");unique[key]=p;
        }
        if(unique.Count>20000)throw new InvalidDataException("통합 지형점 20000개 초과. 간격을 늘리세요.");
        var local=unique.Values.Select(s.Frame.ToLocal).ToList();
        if(local.Any(p=>Math.Abs(p.X)>10000 || Math.Abs(p.Y)>10000 || Math.Abs(p.Z)>10000))throw new InvalidDataException("지형이 내부 원점에서 10km 이상입니다. 원점 E/N/EL을 현장 부근으로 설정하세요.");
        var source="SHP:"+string.Join("|",files.Order(StringComparer.OrdinalIgnoreCase));if(!Replace(doc,source,"Terrain"))return;
        Mutate(doc,s,"CivilBIM SHP 지형",()=>
        {
            DeleteGenerated(doc,source,"Terrain");
            var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l=>Math.Abs(l.Elevation)).FirstOrDefault()??throw new InvalidDataException("레벨이 필요합니다.");
            var type=new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).Cast<ToposolidType>().FirstOrDefault()??throw new InvalidDataException("Toposolid 유형이 필요합니다.");
            if(s.TerrainDepth<=0)throw new InvalidDataException("지형 두께는 양수여야 합니다.");
            var typeName=$"CivilBIM 지형 {s.TerrainDepth:G}m";
            type=new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).Cast<ToposolidType>().FirstOrDefault(x=>x.Name==typeName)??(ToposolidType)type.Duplicate(typeName);
            var compound=type.GetCompoundStructure()??throw new InvalidDataException("지형 유형의 복합구조가 없습니다.");
            var layerIndex=compound.GetLayers().Select((layer,index)=>(layer,index)).First(x=>x.layer.Width>0).index;
            var other=compound.GetLayers().Where((_,i)=>i!=layerIndex).Sum(l=>l.Width);
            if(ModelBuilder.Feet(s.TerrainDepth)<=other)throw new InvalidDataException("지형 두께가 다른 구성층보다 작습니다.");
            compound.SetLayerWidth(layerIndex,ModelBuilder.Feet(s.TerrainDepth)-other);type.SetCompoundStructure(compound);
            var topo=Toposolid.Create(doc,local.Select(ModelBuilder.Xyz).ToList(),type.Id,level.Id);
            Storage.Write(topo,new ElementInfo("Terrain","TERRAIN","지형",unique.Values.Max(p=>p.Z),unique.Values.Min(p=>p.Z),0,0,0,"",source,Guid.NewGuid().ToString(),new(),s.Frame,s.Scale));
            topo.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set($"CivilBIM | 1:{s.Scale} | {s.Frame.Crs} | {unique.Count} points");
        });
        TaskDialog.Show("지형 생성 완료",$"{unique.Count}개 점으로 Toposolid를 생성했습니다.\n원본 축척 1:{s.Scale}. 도엽 경계와 좌표 정합을 확인하세요.");
    }
    private string Preview(Settings s)
    {
        var centers=string.IsNullOrWhiteSpace(s.CipLayer)?[]:cadData!.PileCenters(s.CipLayer,s.CenterlineCip,ModelBuilder.Feet(s.PileSpacing));
        return $"CIP {centers.Count}개\nH-Pile {cadData!.Circles.Count(c=>c.Layer==s.HPileLayer)}개\n띠장 중심선 {cadData.Paths.Count(p=>p.Layer==s.WalerLayer)}개\n버팀대 중심선 {cadData.Paths.Count(p=>p.Layer==s.StrutLayer)}개\n토류벽 중심선 {cadData.Paths.Count(p=>p.Layer==s.WallLayer)}개\n표고 단계: {s.WalerElevations}\nCIP 상하단 EL {s.PileTop}~{s.PileBottom}, 직경 {s.Diameter}m";
    }
    private void Shoring(Document doc,Settings s)
    {
        if(s.PileTop<=s.PileBottom || s.Diameter<=0)throw new InvalidDataException("CIP 상하단/직경을 확인하세요.");
        var mapped=new[]{s.CipLayer,s.WalerLayer,s.StrutLayer,s.HPileLayer,s.WallLayer}.Where(x=>!string.IsNullOrWhiteSpace(x)).ToList();
        if(mapped.Count==0 || mapped.Distinct().Count()!=mapped.Count)throw new InvalidDataException("서로 다른 부재별 레이어를 지정하세요.");
        if(mapped.Any(l=>!cadData!.Layers.Contains(l)))throw new InvalidDataException("지정 레이어가 CAD에 없습니다.");
        var source="CAD:"+cad!.UniqueId+":"+cadData!.RegionKey;var kinds=new[]{"CIP","HPile","Waler","Strut","Wall"};if(!Replace(doc,source,kinds))return;
        var batch=Guid.NewGuid().ToString();var created=0;
        Mutate(doc,s,"CivilBIM CAD 가시설",()=>
        {
            DeleteGenerated(doc,source,kinds);
            var centers=string.IsNullOrWhiteSpace(s.CipLayer)?[]:cadData!.PileCenters(s.CipLayer,s.CenterlineCip,ModelBuilder.Feet(s.PileSpacing));
            BoreholeSet? boreholes=s.CipBottomAtSoftRock?JsonSerializer.Deserialize<BoreholeSet>(File.ReadAllText(s.BoreholeJson),BoreholeSet.JsonOptions):null;
            if(s.CipBottomAtSoftRock && boreholes==null)throw new InvalidDataException("검토된 시추자료가 필요합니다.");
            for(var i=0;i<centers.Count;i++)
            {
                var bottom=s.CipBottomAtSoftRock?Geology.SoftRockTop(s.Frame.ToSurvey(ModelBuilder.Meters(centers[i])),boreholes!,s.Frame.Crs,s.Radius):s.PileBottom;
                if(bottom>=s.PileTop)throw new InvalidDataException("CIP 선단이 상단보다 높습니다.");
                ModelBuilder.Pile(doc,centers[i],new($"CIP-{i+1:D5}","미분할",s.PileTop,bottom,s.Diameter,""),s,source,batch);created++;
            }
            var index=0;
            foreach(var circle in cadData!.Circles.Where(c=>!string.IsNullOrWhiteSpace(s.HPileLayer)&&c.Layer==s.HPileLayer))
            {
                var a=new XYZ(circle.Center.X,circle.Center.Y,ModelBuilder.Feet(s.PileBottom-s.Frame.Elevation));var b=new XYZ(a.X,a.Y,ModelBuilder.Feet(s.PileTop-s.Frame.Elevation));
                var instance=FamilyBuilder.Vertical(doc,s.HPileFamily,circle.Center,s.PileTop,s.PileBottom,s,s.HPileLengthParameter);
                FamilyBuilder.Track(doc,instance,new ElementInfo("HPile",$"HP-{++index:D5}","형강",s.PileTop,s.PileBottom,0,0,s.PileTop-s.PileBottom,"",source,batch,s.Frame.ToSurvey(ModelBuilder.Meters(a)),s.Frame,s.Scale));created++;
            }
            var elevations=s.WalerElevations.Split(',',StringSplitOptions.RemoveEmptyEntries).Select(Guard.Number).Distinct().ToList();foreach(var el in elevations)Guard.Finite(el);
            foreach(var (layer,kind) in new[]{(s.WalerLayer,"Waler"),(s.StrutLayer,"Strut")})
            {
                if(string.IsNullOrWhiteSpace(layer))continue;
                foreach(var path in cadData.Paths.Where(p=>p.Layer==layer))for(var i=1;i<path.Points.Count;i++)foreach(var el in elevations)
                {
                    var a=new XYZ(path.Points[i-1].X,path.Points[i-1].Y,ModelBuilder.Feet(el-s.Frame.Elevation));var b=new XYZ(path.Points[i].X,path.Points[i].Y,a.Z);
                    if(a.DistanceTo(b)<doc.Application.ShortCurveTolerance)throw new InvalidDataException("형강 중심선에 너무 짧은 선분이 있습니다.");
                    var instance=FamilyBuilder.Horizontal(doc,kind=="Waler"?s.WalerFamily:s.StrutFamily,a,b);
                    FamilyBuilder.Track(doc,instance,new ElementInfo(kind,$"{kind}-{++index:D5}","형강",el,el,0,0,a.DistanceTo(b)*.3048,"",source,batch,s.Frame.ToSurvey(ModelBuilder.Meters(a)),s.Frame,s.Scale));created++;
                    if(created>15000)throw new InvalidDataException("부재 수 15000개 초과. 전용 중심선 레이어를 사용하세요.");
                }
            }
            if(s.WallThickness<=0)throw new InvalidDataException("토류벽 두께는 양수여야 합니다.");
            foreach(var path in cadData.Paths.Where(p=>!string.IsNullOrWhiteSpace(s.WallLayer)&&p.Layer==s.WallLayer))for(var i=1;i<path.Points.Count;i++)
            {
                var a=new XYZ(path.Points[i-1].X,path.Points[i-1].Y,ModelBuilder.Feet(s.PileBottom-s.Frame.Elevation));var b=new XYZ(path.Points[i].X,path.Points[i].Y,a.Z);
                var instance=FamilyBuilder.Wall(doc,s.WallFamily,a,b,s.PileTop-s.PileBottom,s.WallHeightParameter);
                FamilyBuilder.Track(doc,instance,new ElementInfo("Wall",$"WALL-{++index:D5}","토류벽",s.PileTop,s.PileBottom,0,0,a.DistanceTo(b)*.3048,"",source,batch,s.Frame.ToSurvey(ModelBuilder.Meters(a)),s.Frame,s.Scale));created++;
            }
            if(created==0)throw new InvalidDataException("매핑한 레이어에서 생성할 형상을 찾지 못했습니다. 원 또는 전용 중심선 레이어를 확인하세요.");
        });TaskDialog.Show("가시설 생성 완료",$"{created}개 요소를 생성했습니다. CAD 중복선, 부재 위치와 제원을 확인하세요.");
    }
    public static void Split(Document doc,Settings s,HashSet<ElementId>? selection=null,bool notify=true)
    {
        var data=JsonSerializer.Deserialize<BoreholeSet>(File.ReadAllText(s.BoreholeJson),BoreholeSet.JsonOptions)??throw new InvalidDataException("시추 JSON 오류.");data.Validate(s.Frame.Crs);
        var piles=Storage.Generated(doc).Where(x=>x.Info.Kind=="CIP").GroupBy(x=>(x.Info.Source,x.Info.Batch,x.Info.ParentId)).Where(g=>selection==null||g.Any(x=>selection.Contains(x.Element.Id))).ToList();
        if(piles.Count==0)throw new InvalidDataException("CivilBIM으로 작성한 CIP가 없습니다.");
        var planned=new List<(List<ElementId> Old,XYZ Center,List<PileSegment> Segments,ElementInfo Info)>();
        foreach(var group in piles)
        {
            foreach(var item in group)ModelBuilder.VerifyUnchanged(item.Element,item.Info);
            var first=group.First().Info;var center=ModelBuilder.Xyz(s.Frame.ToLocal(first.Position));
            var segments=Geology.Split(first.ParentId,first.Position,group.Max(x=>x.Info.Top),group.Min(x=>x.Info.Bottom),first.Diameter,data,s.Frame.Crs,GeologyMethod.Nearest,s.Radius);
            planned.Add((group.Select(x=>x.Element.Id).ToList(),center,segments,first));
        }
        Mutate(doc,s,"CivilBIM CIP 지층 분할",()=>
        {
            foreach(var p in planned)
            {
                var saved=JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(s))!;
                saved.CipLengthParameter=p.Info.LengthParameter;saved.CipDiameterParameter=p.Info.DiameterParameter;saved.CipTopParameter=p.Info.TopParameter;saved.PileOriginAtTop=p.Info.OriginAtTop;
                var oldVolume=p.Old.Select(doc.GetElement).Sum(e=>FamilyBuilder.Solids(e.get_Geometry(new Options())).Sum(ModelBuilder.Volume));
                doc.Delete(p.Old);var created=p.Segments.Select(segment=>ModelBuilder.Pile(doc,p.Center,segment,saved,p.Info.Source,p.Info.Batch,p.Info.FamilySymbol)).ToList();
                var newVolume=created.Sum(e=>Storage.Info(e)!.Quantity);if(Math.Abs(newVolume-oldVolume)>Math.Max(.00001,oldVolume*.0001))throw new InvalidDataException(p.Info.ParentId+": 분할 전후 실제 패밀리 체적이 다릅니다. 패밀리 길이/형상을 확인하세요.");
            }
        });if(notify)TaskDialog.Show("CIP 분할 완료",$"말뚝 {planned.Count}개 → 지층 구간 {planned.Sum(p=>p.Segments.Count)}개. 총길이·체적 보존 검사를 통과했습니다.");
    }
    private void Excavate(Document doc,Settings s)
    {
        var terrain=Storage.Generated(doc).Where(x=>x.Info.Kind=="Terrain").Select(x=>x.Element).OfType<Toposolid>().ToList();
        if(terrain.Count!=1)throw new InvalidDataException("굴착 대상 CivilBIM Toposolid가 정확히 1개여야 합니다.");
        var paths=cadData!.Paths.Where(p=>p.Layer==s.ExcavationLayer && p.Closed).ToList();
        if(paths.Count!=1)throw new InvalidDataException("굴착 레이어에 폐합 폴리선 1개가 필요합니다.");
        var source="EXC:"+cad!.UniqueId;if(!Replace(doc,source,"Excavation"))return;
        Mutate(doc,s,"CivilBIM 굴착",()=>
        {
            DeleteGenerated(doc,source,"Excavation");
            var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l=>Math.Abs(l.Elevation)).First();
            var floorType=new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().First(t=>!t.IsFoundationSlab);
            var vertices=paths[0].Points.Take(paths[0].Points.Count-1).Select(p=>new XYZ(p.X,p.Y,0)).ToList();
            var floor=Floor.Create(doc,new[]{ModelBuilder.Loop(vertices)},floorType.Id,level.Id);
            floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).Set(ModelBuilder.Feet(s.ExcavationElevation-s.Frame.Elevation)-level.Elevation);
            doc.Regenerate();if(!terrain[0].CanBeExcavatedBy(floor.Id))throw new InvalidDataException("이 바닥 요소로 Toposolid 굴착을 적용할 수 없습니다.");
            terrain[0].ExcavateBy(floor.Id);doc.Regenerate();
            Storage.Write(floor,new ElementInfo("Excavation","EXC-1","굴착바닥",s.ExcavationElevation,s.ExcavationElevation,0,0,0,"",source,Guid.NewGuid().ToString(),new(),s.Frame,s.Scale));
        });TaskDialog.Show("굴착 적용","Toposolid에 굴착 바닥을 적용했습니다. 단면에서 바닥 표고와 지형 두께를 확인하세요. 암종별 토공량은 아직 산출하지 않습니다.");
    }
    private static void Export(Document doc)
    {
        var save=new SaveFileDialog {Filter="CSV|*.csv",FileName="CivilBIM-quantities.csv"};if(save.ShowDialog()!=true)return;
        static string Q(object? value)=>"\""+Convert.ToString(value,CultureInfo.InvariantCulture)?.Replace("\"","\"\"")+"\"";
        var csv=new StringBuilder("ElementId,Kind,ParentId,Layer,TopEL_m,BottomEL_m,Diameter_m,Length_m,Volume_m3,Boreholes,Source,Scale,CRS\r\n");
        foreach(var (e,i) in Storage.Generated(doc).Where(x=>x.Info.Kind is "CIP" or "HPile" or "Waler" or "Strut" or "Wall"))
        {
            ModelBuilder.VerifyUnchanged(e,i);
            csv.AppendLine(string.Join(",",new object[]{e.Id.Value,i.Kind,i.ParentId,i.Layer,i.Top,i.Bottom,i.Diameter,i.Length,i.Quantity,i.Boreholes,i.Source,i.Scale,i.Frame.Crs}.Select(Q)));
        }
        File.WriteAllText(save.FileName,csv.ToString(),new UTF8Encoding(true));TaskDialog.Show("물량 내보내기",save.FileName);
    }
    private sealed class CadFilter : ISelectionFilter
    {public bool AllowElement(Element e)=>e is ImportInstance;public bool AllowReference(Reference r,XYZ p)=>false;}
}

