using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CivilBim.Core;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ComboBox=System.Windows.Controls.ComboBox;

namespace CivilBim.Revit;

// The section and its original member list travel with the RVT, not the dialog.
public sealed record SectionMembers(List<string> Members);
public static class SectionSheetData
{
    private static readonly Guid Id=new("acbb9931-f343-4eb4-a1a7-83753b229914");
    private static Schema Schema()
    {
        var s=Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(Id);if(s!=null)return s;
        var b=new SchemaBuilder(Id);b.SetSchemaName("CivilBimSectionMembers");b.AddSimpleField("Json",typeof(string));return b.Finish();
    }
    public static void Write(View view,SectionMembers data){var e=new Entity(Schema());e.Set("Json",JsonSerializer.Serialize(data));view.SetEntity(e);}
    public static SectionMembers? Read(View view){var s=Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(Id);if(s==null)return null;var e=view.GetEntity(s);return e.IsValid()?JsonSerializer.Deserialize<SectionMembers>(e.Get<string>("Json")):null;}
    public static Transform ToSheet(View view,Viewport viewport)
    {
        var transforms=view.GetModelToProjectionTransforms();
        if(transforms.Count!=1)throw new InvalidDataException("분할 자르기 영역은 지원하지 않습니다. 단면의 자르기 분할을 해제하세요.");
        return viewport.GetProjectionToSheetTransform().Multiply(transforms.Single().GetModelToProjectionTransform());
    }
}

public sealed class SectionSheetWindow:ModelessWindow
{
    private readonly Document doc;
    private List<Point3> path=[];
    private List<ElementId> targets=[];
    private List<ElementId> pathElements=[];
    private List<ElementId> lines=[];
    private ElementId lineView=ElementId.InvalidElementId;
    private int planScale=100;
    private readonly ComboBox end=new(){ItemsSource=new[]{"하단을 선까지 맞추기","상단을 선까지 맞추기"},SelectedIndex=0,Margin=new(0,8,0,8),MinHeight=30};
    private readonly TextBlock info=new(){TextWrapping=TextWrapping.Wrap,Margin=new(0,10,0,10)};
    public SectionSheetWindow(Document document)
    {
        doc=document;Title="경로 단면 · 시트 배치 · 선까지 높이 맞춤";Width=670;Height=580;MaxHeight=SystemParameters.WorkArea.Height-35;
        var panel=new StackPanel {Margin=new(22)};Content=new ScrollViewer {Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        panel.Children.Add(new TextBlock {Text="경로를 펼쳐 보고, 그린 선까지 높이 맞추기",FontSize=21,TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(new TextBlock {Text="처음: 평면에서 1 → 2 → 3. 이미 만든 CB 시트/단면이 있으면 그 뷰를 열고 4 → 5만 진행하세요. 이 창은 기존 부재의 높이를 조절합니다. CIP/H-PILE 배치는 먼저 완료하세요.",TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,8)});
        Button Add(string caption,Action<UIApplication> action,bool pick=false){var b=new Button{Content=caption,Padding=new(9),Margin=new(0,5,0,5)};panel.Children.Add(b);b.Click+=(_,_)=>Api(action,pick);return b;}
        Add("1. CAD 폴리선으로 평면 경로 선택",app=>{PlanView(app);TaskDialog.Show("경로 선택","평면에서 전개할 CAD 폴리선을 선택하세요.");path=ConnectedPath.Cad(DrawingSelections.Path(app.ActiveUIDocument,"전개 경로 선택"));pathElements=[];Summary();},true);
        Add("1. Revit 경로선 여러 개 선택 → 완료",app=>{PlanView(app);TaskDialog.Show("Revit 경로선 선택","연결된 모델선/상세선을 여러 개 선택한 뒤 완료를 누르세요. 선택 순서와 관계없이 끝점을 연결합니다. 갈라진 선이나 다른 경로는 함께 선택하지 마세요.");var picked=app.ActiveUIDocument.Selection.PickObjects(ObjectType.Element,new LineFilter(),"연결할 경로선 여러 개 선택 후 완료");path=ConnectedPath.Join(picked.DistinctBy(r=>r.ElementId).Select(r=>(IReadOnlyList<Point3>)((CurveElement)doc.GetElement(r)).GeometryCurve.Tessellate().Select(ModelBuilder.Meters).ToList()));pathElements=picked.Select(r=>r.ElementId).Distinct().ToList();Summary();},true);
        Add("2. 높이를 조절할 CIP / H-PILE 선택",app=>{TaskDialog.Show("수직부재 선택","단면에 표시하고 높이를 조절할 CivilBIM CIP/H-PILE을 선택한 뒤 완료를 누르세요.");targets=app.ActiveUIDocument.Selection.PickObjects(ObjectType.Element,new PileFilter(),"CIP/H-PILE 선택").Select(r=>r.ElementId).Distinct().ToList();Summary();},true);
        panel.Children.Add(info);
        Add("선택한 경로선 표시",app=>{if(pathElements.Count==0)throw new InvalidDataException("화면 표시할 Revit 경로선이 없습니다. CAD 경로는 구간 수로 확인하세요.");app.ActiveUIDocument.Selection.SetElementIds(pathElements);});
        Add("선택한 수직부재 표시",app=>{if(targets.Count==0)throw new InvalidDataException("선택된 수직부재가 없습니다.");app.ActiveUIDocument.Selection.SetElementIds(targets);});
        Add("3. 경로별 단면 생성 + 새 시트에 연속 배치",Create);
        panel.Children.Add(new TextBlock {Text="3번 후 열린 시트에서 단면 안을 더블클릭해 활성화하세요. 해당 구간의 전개도를 같은 축척·EL로 맞추고 상단 또는 하단을 따라 Revit 상세선을 그립니다. 그 단면을 활성화한 상태에서 4번을 누르세요. 시트 전체에 이어 그릴 때는 뷰 비활성화 후 시트 상세선을 사용합니다. CAD 선은 4번에서 직접 선택되지 않습니다.",TextWrapping=TextWrapping.Wrap,Margin=new(0,10,0,10)});
        panel.Children.Add(end);
        Add("4. 높이 기준선 선택",app=>{var ui=app.ActiveUIDocument;TaskDialog.Show("높이 기준선 선택","활성 단면의 모델선/상세선 또는 시트 위 상세선을 선택하세요. 여러 조각도 선택할 수 있습니다.");var picked=ui.Selection.PickObjects(ObjectType.Element,new LineFilter(),"높이 기준선 선택");lines=picked.Select(r=>r.ElementId).ToList();lineView=ui.ActiveView.Id;Summary();},true);
        var apply=Add("5. 선택한 선까지 CIP / H-PILE 높이 적용",Apply);apply.Background=System.Windows.Media.Brushes.RoyalBlue;apply.Foreground=System.Windows.Media.Brushes.White;StickyActions(apply);Summary();
    }
    private void Summary()=>info.Text=$"선택 현황: 경로 {Math.Max(0,path.Count-1)}구간 / Revit 경로선 {pathElements.Count}개 / 수직부재 {targets.Count}개 / 기준선 {lines.Count}개. 만든 단면과 부재 연결은 RVT에 저장됩니다.";
    private void PlanView(UIApplication app){var active=app.ActiveUIDocument.ActiveView;if(active is not ViewPlan)throw new InvalidDataException("평면 뷰로 전환한 뒤 경로를 선택하세요.");planScale=Math.Max(1,active.Scale);}
    internal void ConfigureCheck(List<Point3> route,IEnumerable<ElementId> ids,int scale=100){path=route;targets=ids.ToList();planScale=scale;Summary();}
    internal void CreateCheck(UIApplication app)=>Create(app);
    internal void ApplyCheck(UIApplication app,ElementId source,IEnumerable<ElementId> curveIds,bool top){lineView=source;lines=curveIds.ToList();end.SelectedIndex=top?1:0;Apply(app);}
    private sealed class PileFilter:ISelectionFilter {public bool AllowElement(Element e)=>e is FamilyInstance;public bool AllowReference(Reference r,XYZ p)=>false;}
    private sealed class LineFilter:ISelectionFilter {public bool AllowElement(Element e)=>e is CurveElement;public bool AllowReference(Reference r,XYZ p)=>false;}
    private void Create(UIApplication app)
    {
        if(path.Count<2||targets.Count==0)throw new InvalidDataException("경로와 수직부재를 먼저 선택하세요.");
        var segments=path.Zip(path.Skip(1),(a,b)=>(A:ModelBuilder.Xyz(a),B:ModelBuilder.Xyz(b))).ToList();
        if(segments.Any(s=>new XYZ(s.B.X-s.A.X,s.B.Y-s.A.Y,0).GetLength()<doc.Application.ShortCurveTolerance))throw new InvalidDataException("경로에 너무 짧은 구간이 있습니다.");
        var piles=targets.Select(id=>doc.GetElement(id) as FamilyInstance??throw new InvalidDataException("선택 부재가 삭제되었습니다.")).ToList();
        var siblings=Storage.Generated(doc).Where(p=>p.Info.Kind is "CIP" or "HPile").GroupBy(p=>(p.Info.Source,p.Info.Batch,p.Info.ParentId)).ToDictionary(g=>g.Key,g=>g.Count());
        foreach(var pile in piles){var data=Storage.Info(pile);if(data!=null&&siblings.GetValueOrDefault((data.Source,data.Batch,data.ParentId))>1)throw new InvalidDataException("지층으로 분할하기 전 원말뚝으로 단면을 생성하세요.");}
        using var group=new TransactionGroup(doc,"독립 수직부재 경로 단면 작성");group.Start();var released=0;
        using(var register=new Transaction(doc,"기존 수직 패밀리 연결"))
        {
            register.Start();var settings=Storage.Load(doc);
            foreach(var pile in piles.Where(p=>Storage.Info(p)==null))
            {
                var b=FamilyBuilder.Bounds(pile);var length=(b.Max.Z-b.Min.Z)*.3048;
                var candidates=pile.Parameters.Cast<Parameter>().Where(p=>!p.IsReadOnly&&p.StorageType==StorageType.Double&&p.Definition.GetDataType()==SpecTypeId.Length&&Math.Abs(p.AsDouble()*.3048-length)<.002).ToList();
                if(pile.Symbol.Family.FamilyPlacementType!=FamilyPlacementType.TwoLevelsBased&&candidates.Count!=1)throw new InvalidDataException($"부재 {pile.Id}: 실제 높이와 일치하는 수정 가능한 길이 파라미터를 하나로 확인할 수 없습니다. 패밀리 길이 파라미터를 확인하세요.");
                var info=new ElementInfo("HPile",pile.UniqueId,"",b.Max.Z*.3048+settings.Frame.Elevation,b.Min.Z*.3048+settings.Frame.Elevation,0,0,length,"","기존 패밀리",pile.UniqueId,ModelBuilder.Meters((b.Min+b.Max)/2),settings.Frame,settings.Scale){LengthParameter=candidates.FirstOrDefault()?.Definition.Name??""};
                Storage.Write(pile,info);
            }
            if(register.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("기존 부재 연결을 취소했습니다.");
        }
        using(var prepare=new Transaction(doc,"CIP H-PILE 자동결합 해제")){prepare.Start();released=FamilyBuilder.ReleaseVerticalJoins(doc,piles);if(prepare.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("자동결합 해제를 취소했습니다.");}
        var bounds=piles.ToDictionary(p=>p.Id,FamilyBuilder.Bounds);var assignment=segments.Select(_=>new List<FamilyInstance>()).ToList();
        double farthest=0;
        foreach(var pile in piles)
        {
            var nearest=ConnectedPath.Nearest(ModelBuilder.Meters(Center(bounds[pile.Id])),path);farthest=Math.Max(farthest,nearest.Distance);assignment[nearest.Segment].Add(pile);
        }
        var boxes=bounds.Values.ToList();var low=boxes.Min(b=>b.Min.Z)-ModelBuilder.Feet(3);var high=boxes.Max(b=>b.Max.Z)+ModelBuilder.Feet(3);
        var type=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(v=>v.ViewFamily==ViewFamily.Section)??throw new InvalidDataException("단면 뷰 유형이 없습니다.");
        // A DWG linked directly to the sheet in millimetres is full-size sheet
        // geometry. Use 1:1 for this working sheet so both share the same scale.
        const int scale=1;
        using var transaction=new Transaction(doc,"경로 단면 생성 및 시트 연속 배치");transaction.Start();
        var phases=doc.Phases.Cast<Phase>().ToList();var selectedPhases=piles.Select(p=>p.CreatedPhaseId).ToHashSet();var phase=phases.LastOrDefault(p=>selectedPhases.Contains(p.Id));
        var phaseFilter=new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>().FirstOrDefault(f=>f.GetPhaseStatusPresentation(ElementOnPhaseStatus.New)!=PhaseStatusPresentation.DontShow&&f.GetPhaseStatusPresentation(ElementOnPhaseStatus.Existing)!=PhaseStatusPresentation.DontShow);
        var generated=Storage.Generated(doc).Select(g=>g.Element).ToList();var imports=new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).ToElements();
        var sheet=ViewSheet.Create(doc,ElementId.InvalidElementId);sheet.Name="CivilBIM 경로 전개도";sheet.SheetNumber="CB-"+sheet.Id.Value;
        var cursor=ModelBuilder.Feet(.05);var originY=ModelBuilder.Feet(.05);var serial=0;
        foreach(var s in segments)
        {
            var x=new XYZ(s.B.X-s.A.X,s.B.Y-s.A.Y,0).Normalize();var normal=x.CrossProduct(XYZ.BasisZ);var length=new XYZ(s.B.X-s.A.X,s.B.Y-s.A.Y,0).GetLength();
            var memberPoints=assignment[serial].SelectMany(p=>Corners(bounds[p.Id])).ToList();
            var minAlong=Math.Min(0,memberPoints.Count==0?0:memberPoints.Min(p=>(p-s.A).DotProduct(x)));var maxAlong=Math.Max(length,memberPoints.Count==0?length:memberPoints.Max(p=>(p-s.A).DotProduct(x)));
            var depth=Math.Max(ModelBuilder.Feet(1),memberPoints.Count==0?0:memberPoints.Max(p=>Math.Abs((p-s.A).DotProduct(normal)))+ModelBuilder.Feet(.1));
            // CreateSection's viewing direction is BasisZ; Revit computes RightDirection.
            var transform=Transform.Identity;transform.Origin=new(s.A.X,s.A.Y,0);transform.BasisX=x;transform.BasisY=XYZ.BasisZ;transform.BasisZ=normal;
            var box=new BoundingBoxXYZ {Transform=transform,Min=new(minAlong,low,-depth),Max=new(maxAlong,high,depth)};
            var view=ViewSection.CreateSection(doc,type.Id,box);
            if(view.RightDirection.DotProduct(x)<0)
            {
                doc.Delete(view.Id);transform.Origin=new(s.B.X,s.B.Y,0);transform.BasisX=-x;transform.BasisZ=-normal;box.Transform=transform;box.Min=new(length-maxAlong,low,-depth);box.Max=new(length-minAlong,high,depth);view=ViewSection.CreateSection(doc,type.Id,box);
            }
            if(view.RightDirection.DotProduct(x)<.999)throw new InvalidDataException("단면의 좌우 방향을 경로와 일치시키지 못했습니다.");
            view.Name=$"CB 전개 {sheet.Id.Value}-{++serial:00}";view.Scale=scale;view.DetailLevel=ViewDetailLevel.Fine;view.CropBoxActive=true;view.CropBoxVisible=false;
            view.ViewTemplateId=ElementId.InvalidElementId;
            if(phase!=null)view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.Set(phase.Id);
            if(phaseFilter!=null)view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.Set(phaseFilter.Id);
            foreach(var category in piles.Select(p=>p.Category.Id).Distinct())if(view.CanCategoryBeHidden(category))view.SetCategoryHidden(category,false);
            var assigned=assignment[serial-1].Select(p=>p.Id).ToHashSet();var hidden=generated.Concat(imports).Where(e=>!assigned.Contains(e.Id)&&e.CanBeHidden(view)).Select(e=>e.Id).Distinct().ToList();if(hidden.Count>0)view.HideElements(hidden);
            view.SketchPlane=SketchPlane.Create(doc,Plane.CreateByNormalAndOrigin(view.ViewDirection,view.Origin));
            SectionSheetData.Write(view,new(assignment[serial-1].Select(p=>p.UniqueId).ToList()));
            doc.Regenerate();
            var viewport=Viewport.Create(doc,sheet.Id,view.Id,XYZ.Zero);doc.Regenerate();
            var map=SectionSheetData.ToSheet(view,viewport);var a=map.OfPoint(new XYZ(s.A.X,s.A.Y,low));
            // Crop extensions show offset members, but must not add length to
            // the developed path. Adjacent route endpoints share one station.
            var move=new XYZ(cursor-a.X,originY-a.Y,0);viewport.SetBoxCenter(viewport.GetBoxCenter()+move);cursor+=length/scale;
        }
        if(transaction.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("단면/시트 생성을 취소했습니다.");
        group.Assimilate();app.ActiveUIDocument.RequestViewChange(sheet);SetStatus($"1:1 작업 시트: 단면 {segments.Count}개 / 부재 {piles.Count}개 연결. CAD를 mm로 시트에 링크하고 경로 시작점과 EL을 맞추세요. 최대 이격 {farthest:F3}m.");
    }
    private static XYZ Center(BoundingBoxXYZ bounds)=>bounds.Transform.OfPoint((bounds.Min+bounds.Max)/2);
    private static XYZ Position(FamilyInstance pile)=>Center(FamilyBuilder.Bounds(pile));
    private static IEnumerable<XYZ> Corners(BoundingBoxXYZ b)=>from x in new[]{b.Min.X,b.Max.X} from y in new[]{b.Min.Y,b.Max.Y} from z in new[]{b.Min.Z,b.Max.Z} select b.Transform.OfPoint(new XYZ(x,y,z));
    private void Apply(UIApplication app)
    {
        if(lines.Count==0||doc.GetElement(lineView) is not View source)throw new InvalidDataException("높이 기준선을 먼저 선택하세요.");
        var curves=lines.Select(id=>doc.GetElement(id) as CurveElement??throw new InvalidDataException("기준선이 삭제되었습니다. 다시 선택하세요.")).ToList();
        if(curves.Any(c=>c is DetailCurve&&c.OwnerViewId!=source.Id))throw new InvalidDataException("기준선과 활성 뷰가 다릅니다. 시트 상세선은 시트에서, 단면 선은 단면을 활성화한 뒤 선택하세요.");
        var contexts=new List<(View View,Transform Map)>();
        if(source is ViewSheet sheet)
        {
            foreach(var id in sheet.GetAllViewports()){var vp=(Viewport)doc.GetElement(id);var view=(View)doc.GetElement(vp.ViewId);if(SectionSheetData.Read(view)!=null)contexts.Add((view,SectionSheetData.ToSheet(view,vp)));}
        }
        else if(source is ViewSection&&SectionSheetData.Read(source)!=null)contexts.Add((source,Transform.Identity));
        else throw new InvalidDataException("이 기능으로 만든 단면 또는 그 단면이 배치된 시트에서 선을 선택하세요.");
        var plans=new List<(FamilyInstance Pile,ElementInfo Info,double Top,double Bottom)>();var settings=Storage.Load(doc);
        foreach(var (view,map) in contexts)
        {
            if(Math.Abs(view.UpDirection.Z)<.999)throw new InvalidDataException("수직 단면만 높이를 맞출 수 있습니다.");
            var projected=curves.SelectMany(c=>{var pts=c.GeometryCurve.Tessellate();return pts.Zip(pts.Skip(1),(a,b)=>(A:a,B:b));}).ToList();
            foreach(var uid in SectionSheetData.Read(view)!.Members)
            {
                var pile=doc.GetElement(uid) as FamilyInstance??throw new InvalidDataException("단면에 연결된 부재가 삭제되었습니다. 단면을 다시 생성하세요.");var info=Storage.Info(pile)??throw new InvalidDataException("부재 정보가 없습니다.");
                var live=FamilyBuilder.Bounds(pile);info=info with {Top=live.Max.Z*.3048+settings.Frame.Elevation,Bottom=live.Min.Z*.3048+settings.Frame.Elevation};
                var p=Position(pile);var basePoint=new XYZ(p.X,p.Y,0);var q=map.OfPoint(basePoint);var up=map.OfVector(XYZ.BasisZ);var right=source is ViewSheet?new XYZ(up.Y,-up.X,0).Normalize():view.RightDirection;
                var profile=new List<PlanSegment>();
                foreach(var (a,b) in projected)
                {
                    if(source is not ViewSheet&&(Math.Abs((a-view.Origin).DotProduct(view.ViewDirection))>ModelBuilder.Feet(.02)||Math.Abs((b-view.Origin).DotProduct(view.ViewDirection))>ModelBuilder.Feet(.02)))continue;
                    Point3 Project(XYZ v)=>new((v-q).DotProduct(right),(v-q).DotProduct(up)/up.DotProduct(up)*.3048);
                    profile.Add(new(Project(a),Project(b)));
                }
                var height=SectionLineHeight.At(0,profile);if(height==null)continue;
                var el=height.Value+settings.Frame.Elevation;var top=end.SelectedIndex==1?el:info.Top;var bottom=end.SelectedIndex==0?el:info.Bottom;
                if(top<=bottom)throw new InvalidDataException($"부재 {pile.Id}: 상단보다 하단이 높습니다.");plans.Add((pile,info,top,bottom));
            }
        }
        if(plans.Count==0)throw new InvalidDataException("기준선과 겹치는 연결 부재가 없습니다. 선을 부재 위치까지 연장하세요.");
        if(plans.GroupBy(p=>p.Pile.Id).Any(g=>g.Count()>1))throw new InvalidDataException("한 부재가 여러 전개 단면에 연결되어 있습니다. 적용할 단면을 활성화하고 선을 선택하세요.");
        using var group=new TransactionGroup(doc,"전개도 높이 적용 및 실제 형상 검산");group.Start();
        using var transaction=new Transaction(doc,"전개 단면 기준선까지 부재 높이 조정");transaction.Start();foreach(var p in plans)FamilyBuilder.Resize(doc,p.Pile,p.Info,p.Top,p.Bottom,settings);
        if(transaction.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("높이 적용을 취소했습니다.");
        FamilyBuilder.FinalizeVertical(doc,plans.Select(p=>p.Pile),settings);group.Assimilate();SetStatus($"{plans.Count}개 부재의 {(end.SelectedIndex==0?"하단":"상단")}을 기준선까지 조정하고 실제 높이를 검산했습니다. 나머지 끝 높이는 유지했습니다.");
    }
}
