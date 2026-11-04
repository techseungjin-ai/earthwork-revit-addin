using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CivilBim.Core;

namespace CivilBim.Revit;

// Separate revision host only: generated test families are labelled fixtures, never substitutes for user RFAs.
public static class PlacementChecks
{
    public static void Run(UIApplication ui,Document doc,Settings settings,string output,List<string> log)
    {
        SiteEdits(doc,log);
        var vertical=CreateFamily(ui,false,output);var horizontal=CreateFamily(ui,true,output);
        var origin=new XYZ(ModelBuilder.Feet(-20),ModelBuilder.Feet(-20),0);
        using var t=new Transaction(doc,"패밀리 배치 검증 fixture");t.Start();
        if(!doc.LoadFamily(vertical,out var vf)||!doc.LoadFamily(horizontal,out var hf))throw new InvalidDataException("검증 fixture 패밀리 로드 실패.");
        var vs=(FamilySymbol)doc.GetElement(vf.GetFamilySymbolIds().First());var hs=(FamilySymbol)doc.GetElement(hf.GetFamilySymbolIds().First());
        var reader=new CadReader();
        foreach(var (angle,index) in new[]{0.0,45.0,90.0}.Select((a,i)=>(a,i)))
        {
            XYZ Point(double x,double y)=>origin+new XYZ(ModelBuilder.Feet(index*2+x*Math.Cos(angle*Math.PI/180)-y*Math.Sin(angle*Math.PI/180)),ModelBuilder.Feet(x*Math.Sin(angle*Math.PI/180)+y*Math.Cos(angle*Math.PI/180)),0);
            reader.Paths.Add(new("HP",[Point(-.15,-.1),Point(.15,-.1)],false));reader.Paths.Add(new("HP",[Point(-.15,.1),Point(.15,.1)],false));reader.Paths.Add(new("HP",[Point(0,-.1),Point(0,.1)],false));
        }
        var s=new Settings {Frame=settings.Frame,HPileLayer="HP",HPileFamily=vs.UniqueId,HPileLengthParameter="Depth",PileTop=10,PileBottom=-15,Scale=settings.Scale};
        var instances=VerticalService.Place(doc,reader,"H-PILE",s,"FIXTURE");
        if(instances.Count!=3)throw new InvalidDataException("H-PILE 3개 인식/배치 실패.");
        foreach(var (instance,index) in instances.Select((x,i)=>(x,i)))
        {
            var angle=index*Math.PI/4;var actual=((LocationPoint)instance.Location).Rotation;
            if(Math.Abs(PlanRecognition.Normalize(actual)-PlanRecognition.Normalize(angle))>1e-6)throw new InvalidDataException("H-PILE 실제 인스턴스 회전 불일치.");
            var id=instance.UniqueId;FamilyBuilder.Resize(doc,instance,Storage.Info(instance)!,12,-18-index,s);doc.Regenerate();var bounds=FamilyBuilder.Bounds(instance);
            if(instance.UniqueId!=id||Math.Abs(bounds.Min.Z*.3048-(-18-index-settings.Frame.Elevation))>.002||Math.Abs(bounds.Max.Z*.3048-(12-settings.Frame.Elevation))>.002)throw new InvalidDataException("전개도 깊이 변경 실제 경계 불일치.");
        }
        var lines=new[]{new PlanSegment(new(-20,-20),new(-16,-16)),new PlanSegment(new(-20,-18),new(-16,-14))};
        var beams=HorizontalService.Place(doc,lines,new[]{8.0,3.0,-2.0},hs.UniqueId,s,"FIXTURE");
        if(beams.Count!=6)throw new InvalidDataException("STRUT 2개×3단 배치 실패.");
        foreach(var beam in beams){var box=FamilyBuilder.Bounds(beam);var center=(box.Min.Z+box.Max.Z)/2*.3048+settings.Frame.Elevation;if(Math.Abs(center-Storage.Info(beam)!.Top)>.002)throw new InvalidDataException("STRUT 실제 솔리드의 단별 중심 EL 불일치.");}
        var view=Views.Create(doc,"04_패밀리 배치 검증용 예시",new[]{new Point3(-22,-22,-20),new Point3(-12,-12,13)});
        foreach(var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())if(e.Id!=view.Id&&e.CanBeHidden(view)&&Storage.Info(e)?.Source!="FIXTURE")view.HideElements(new[]{e.Id});
        var fixtureIds=instances.Concat(beams).Select(x=>x.Id).ToList();
        foreach(var other in new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Where(v=>v.Id!=view.Id&&!v.IsTemplate))other.HideElements(fixtureIds.Where(id=>doc.GetElement(id).CanBeHidden(other)).ToList());
        t.Commit();RevisionRunner.Export(doc,view,Path.Combine(output,"04-검증용패밀리-회전-깊이-3단"));
        log.Add("별도 생성한 검증 RFA: H-PILE 0/45/90도 실제 회전, UID 유지 깊이 변경, STRUT 2개×3단=6개 실제 배치 통과. 고객 RFA 호환성은 별도 확인 필요.");
    }
    private static void SiteEdits(Document doc,List<string> log)
    {
        using var t=new Transaction(doc,"지형 수정 검증 — 완료 후 되돌림");t.Start();t.SetFailureHandlingOptions(t.GetFailureHandlingOptions().SetFailuresPreprocessor(new ContextFailures(log)).SetClearAfterRollback(true));
        var type=new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).Cast<ToposolidType>().First();var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l=>Math.Abs(l.Elevation)).First();
        XYZ P(double x,double y,double z=10)=>ModelBuilder.Xyz(new(x+1000,y+1000,z));
        var terrain=Toposolid.Create(doc,new[]{P(0,0),P(10,0),P(10,10),P(0,10),P(5,5)}.ToList(),type.Id,level.Id);doc.Regenerate();
        var count=SiteEditing.Move(terrain,P(4.9,4.9),P(5.1,5.1),ModelBuilder.Xyz(new(.2,.1)));
        if(count!=1||ContextBuilder.Surface(terrain).Elevation(1005.2,1005.1)==null)throw new InvalidDataException("지형 내부점 XY 이동 검증 실패.");
        var view=new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(v=>!v.IsTemplate);var textType=new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).FirstElementId();
        var note=TextNote.Create(doc,view.Id,P(5.2,5.1,0),"EL +12.0",textType);SiteEditing.MapElevations(terrain,new[]{note},0);
        if(Math.Abs((ContextBuilder.Surface(terrain).Elevation(1005.2,1005.1)??0)-12)>.002)throw new InvalidDataException("측량 TextNote 표고 반영 검증 실패.");
        Toposolid Road(double a,double b)=>terrain.CreateSubDivision(doc,type.Id,new[]{ModelBuilder.Loop(new[]{P(a,1,0),P(b,1,0),P(b,3,0),P(a,3,0)})});
        var r1=Road(1,3);var r2=Road(3,5);doc.Regenerate();var merged=SiteEditing.Merge(doc,new[]{r1,r2});if(merged.Count!=1)throw new InvalidDataException("도로 구획 합치기 검증 실패.");
        t.RollBack();log.Add("Native 지형 수정 검증: 내부점 XY 이동 1점, 측량 TextNote EL12 반영, 인접 도로 2구획→1구획 병합 통과. 테스트 변경은 되돌림.");
    }
    public static void CipSplit(UIApplication ui,Document doc,Settings settings,BoreholeSet borings,string output,List<string> log)
    {
        var file=Path.Combine(output,"검증용_CIP.rfa");
        using(var family=ui.Application.NewFamilyDocument(@"C:\ProgramData\Autodesk\RVT 2027\Family Templates\English\Metric Generic Model.rft"))
        {
            using var t=new Transaction(family,"검증용 원통");t.Start();family.FamilyManager.NewType("검증용 D530");var depth=family.FamilyManager.AddParameter("Depth",GroupTypeId.Geometry,SpecTypeId.Length,true);family.FamilyManager.Set(depth,ModelBuilder.Feet(1));
            var p=new CurveArray();var radius=ModelBuilder.Feet(.265);p.Append(Arc.Create(XYZ.Zero,radius,0,Math.PI,XYZ.BasisX,XYZ.BasisY));p.Append(Arc.Create(XYZ.Zero,radius,Math.PI,2*Math.PI,XYZ.BasisX,XYZ.BasisY));var profiles=new CurveArrArray();profiles.Append(p);
            var plane=SketchPlane.Create(family,Plane.CreateByNormalAndOrigin(XYZ.BasisZ,XYZ.Zero));var extrusion=family.FamilyCreate.NewExtrusion(true,profiles,plane,ModelBuilder.Feet(1));family.FamilyManager.AssociateElementParameterToFamilyParameter(extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM),depth);t.Commit();family.SaveAs(file,new SaveAsOptions {OverwriteExistingFile=true});
        }
        using var group=new TransactionGroup(doc,"CIP 분할 명령 검증 — 완료 후 되돌림");group.Start();FamilyInstance whole;var s=new Settings {Frame=settings.Frame,Scale=settings.Scale,Diameter=.53,CipLengthParameter="Depth",CipDiameterParameter=""};
        var input=Path.Combine(output,"검증용_시추자료.json");File.WriteAllText(input,System.Text.Json.JsonSerializer.Serialize(borings,BoreholeSet.JsonOptions));s.BoreholeJson=input;
        using(var t=new Transaction(doc,"원말뚝 검증"))
        {
            t.Start();doc.LoadFamily(file,out var family);s.CipFamily=doc.GetElement(family.GetFamilySymbolIds().First()).UniqueId;var hole=borings.Boreholes[0];
            whole=ModelBuilder.Pile(doc,ModelBuilder.Xyz(settings.Frame.ToLocal(new(hole.Easting,hole.Northing))),new("TEST-CIP","미분할",hole.GroundElevation-.1,hole.GroundElevation-25,.53,""),s,"CIP-CHECK","CIP-CHECK");t.Commit();
        }
        WorkflowCommand.Split(doc,s,new HashSet<ElementId>{whole.Id},notify:false);var parts=Storage.Generated(doc).Where(x=>x.Info.Source=="CIP-CHECK").ToList();if(parts.Count<3)throw new InvalidDataException("CIP 전용 분할 명령 native 검사 실패.");
        if(parts.Any(x=>x.Info.Boreholes!=borings.Boreholes[0].Id))throw new InvalidDataException("회전 대지의 CIP 최근접 시추공 정합 실패.");
        log.Add($"선택 CIP 실제 분할 명령: 1개→{parts.Count}개, 실제 체적 {parts.Sum(x=>x.Info.Quantity):F9}m³ 보존. 시험 변경은 되돌림.");group.RollBack();
    }
    internal static string CreateFamily(UIApplication ui,bool horizontal,string output)
    {
        var template=horizontal?"Metric Generic Model line based.rft":"Metric Generic Model.rft";
        using var family=ui.Application.NewFamilyDocument(Path.Combine(@"C:\ProgramData\Autodesk\RVT 2027\Family Templates\English",template));
        using(var t=new Transaction(family,"검증용 H형 fixture"))
        {
            t.Start();family.FamilyManager.NewType("검증용 H300x200");
            var length=horizontal?family.FamilyManager.Parameters.Cast<FamilyParameter>().First(p=>p.Id.Value==(long)BuiltInParameter.FAMILY_LINE_LENGTH_PARAM||p.Definition.Name is "Length" or "길이"):family.FamilyManager.AddParameter("Depth",GroupTypeId.Geometry,SpecTypeId.Length,true);
            if(!horizontal)family.FamilyManager.Set(length,ModelBuilder.Feet(1));
            var xy=new[]{(-.15,-.1),(.15,-.1),(.15,-.085),(.0075,-.085),(.0075,.085),(.15,.085),(.15,.1),(-.15,.1),(-.15,.085),(-.0075,.085),(-.0075,-.085),(-.15,-.085)};
            var points=xy.Select(p=>horizontal?new XYZ(0,ModelBuilder.Feet(p.Item1),ModelBuilder.Feet(p.Item2)):new XYZ(ModelBuilder.Feet(p.Item1),ModelBuilder.Feet(p.Item2),0)).ToList();
            var curve=new CurveArray();for(var i=0;i<points.Count;i++)curve.Append(Line.CreateBound(points[i],points[(i+1)%points.Count]));var profile=new CurveArrArray();profile.Append(curve);
            var plane=SketchPlane.Create(family,Plane.CreateByNormalAndOrigin(horizontal?XYZ.BasisX:XYZ.BasisZ,XYZ.Zero));
            var extrusion=family.FamilyCreate.NewExtrusion(true,profile,plane,ModelBuilder.Feet(1));family.FamilyManager.AssociateElementParameterToFamilyParameter(extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM),length);t.Commit();
        }
        var path=Path.Combine(output,horizontal?"검증용_STRUT.rfa":"검증용_H-PILE.rfa");family.SaveAs(path,new SaveAsOptions{OverwriteExistingFile=true});return path;
    }
}
