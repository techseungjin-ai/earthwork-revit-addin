using Autodesk.Revit.DB;

namespace CivilBim.Revit;
public static class OverlapParameters
{
    private static readonly (string Name,ForgeTypeId Type)[] Fields=[("CBIM_중복지층",SpecTypeId.String.Text),("CBIM_현황중복체적",SpecTypeId.Volume),("CBIM_굴착후중복체적",SpecTypeId.Volume),("CBIM_중복체적차",SpecTypeId.Volume),("CBIM_중복기반절토",SpecTypeId.Volume),("CBIM_지층내성토",SpecTypeId.Volume),("CBIM_중복계산대상",SpecTypeId.String.Text),("CBIM_중복계산상태",SpecTypeId.String.Text),("CBIM_중복기록",SpecTypeId.String.Text)];
    private static Guid Id(string name)=>new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("CivilBIM-overlap-v1:"+name)));
    public static void Ensure(Document doc)
    {
        if(Fields.All(f=>SharedParameterElement.Lookup(doc,Id(f.Name))!=null))return;
        var app=doc.Application;var previous=app.SharedParametersFilename;var file=Path.Combine(Path.GetTempPath(),"CivilBIM-overlap-"+Guid.NewGuid().ToString("N")+".txt");
        File.WriteAllText(file,"# Revit shared parameter file.\n*META\tVERSION\tMINVERSION\nMETA\t2\t1\n*GROUP\tID\tNAME\n*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE\n");
        try
        {
            app.SharedParametersFilename=file;var definitions=app.OpenSharedParameterFile()??throw new InvalidDataException("중복체적 파라미터 정의 파일 생성 실패");var group=definitions.Groups.Create("CivilBIM overlap");var categories=app.Create.NewCategorySet();categories.Insert(doc.Settings.Categories.get_Item(BuiltInCategory.OST_GenericModel));
            foreach(var f in Fields){if(SharedParameterElement.Lookup(doc,Id(f.Name))!=null)continue;var definition=group.Definitions.Create(new ExternalDefinitionCreationOptions(f.Name,f.Type){GUID=Id(f.Name),UserModifiable=false,Visible=f.Name!="CBIM_중복기록"});if(!doc.ParameterBindings.Insert(definition,app.Create.NewInstanceBinding(categories),GroupTypeId.Data))throw new InvalidDataException("중복 물량 바인딩 실패: "+f.Name);}
        }finally{app.SharedParametersFilename=previous;}
    }
    public static void Set(Element element,SoilOverlapRow row,Toposolid before,Toposolid after)
    {
        element.get_Parameter(Id("CBIM_중복지층")).Set(row.Layer);
        foreach(var (name,value) in new[]{("CBIM_현황중복체적",row.Before),("CBIM_굴착후중복체적",row.After),("CBIM_중복체적차",row.Difference),("CBIM_중복기반절토",row.Cut),("CBIM_지층내성토",row.Fill)})element.get_Parameter(Id(name)).Set(UnitUtils.ConvertToInternalUnits(value,UnitTypeId.CubicMeters));
        element.get_Parameter(Id("CBIM_중복계산대상")).Set($"현황 {before.Id.Value} / 굴착후 {after.Id.Value}");element.get_Parameter(Id("CBIM_중복계산상태")).Set("계산시점 결과 · 형상 수정후 재계산 / 면적 미산정");
    }
    public static void MarkStale(Element element)=>element.get_Parameter(Id("CBIM_중복계산상태"))?.Set("지형 수정됨 · 중복체적 재계산 필요");
    private sealed record Snapshot(string Signature,double Volume,string Terrain,string Stamp);
    public static void SetSnapshot(Element element,SoilIntersection result,Toposolid terrain,bool baseline)
    {
        var record=element.get_Parameter(Id("CBIM_중복기록"));
        void Volume(string name,double value)=>element.get_Parameter(Id(name)).Set(UnitUtils.ConvertToInternalUnits(value,UnitTypeId.CubicMeters));
        element.get_Parameter(Id("CBIM_중복지층")).Set(result.Layer);
        var stamp=DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss zzz");
        if(baseline)
        {
            record.Set(System.Text.Json.JsonSerializer.Serialize(new Snapshot(result.Signature,result.Volume,terrain.UniqueId,stamp)));
            Volume("CBIM_현황중복체적",result.Volume);Volume("CBIM_굴착후중복체적",0);Volume("CBIM_중복체적차",0);
            element.get_Parameter(Id("CBIM_중복계산상태")).Set("현황만 기록 · 굴착후 값/차이는 미계산 (표시0) · "+stamp);
            element.get_Parameter(Id("CBIM_중복계산대상")).Set($"현황 {terrain.Id.Value} / 굴착후 미기록");
        }
        else
        {
            var json=record.AsString();if(string.IsNullOrEmpty(json))throw new InvalidDataException(result.Layer+": 현황 중복체적을 먼저 기록하세요.");
            var previous=System.Text.Json.JsonSerializer.Deserialize<Snapshot>(json)??throw new InvalidDataException("현황 기록을 읽을 수 없습니다.");
            if(previous.Signature!=result.Signature)throw new InvalidDataException(result.Layer+": 현황 기록 이후 지층 형상/위치가 바뀌었습니다. 같은 지층 형상으로 현황부터 다시 기록하세요.");
            Volume("CBIM_굴착후중복체적",result.Volume);Volume("CBIM_중복체적차",previous.Volume-result.Volume);
            element.get_Parameter(Id("CBIM_중복계산대상")).Set($"현황 {previous.Terrain} ({previous.Stamp}) / 굴착후 {terrain.Id.Value} ({stamp})");
            element.get_Parameter(Id("CBIM_중복계산상태")).Set("현황−굴착후 중복체적차 (m³) · 형상 수정후 재기록 · 면적 미산정");
        }
        // The separate cut/fill computation from the old two-terrain command is not
        // equivalent to this signed snapshot difference. Do not retain stale results.
        Volume("CBIM_중복기반절토",0);Volume("CBIM_지층내성토",0);
    }
    public static ViewSchedule Schedule(Document doc)
    {
        const string name="04_지층별 현황·굴착후 중복 체적";var existing=new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().FirstOrDefault(v=>v.Name==name);if(existing!=null)
        {
            var hidden=Fields.Where(f=>f.Name is "CBIM_중복기반절토" or "CBIM_지층내성토" or "CBIM_중복기록").Select(f=>SharedParameterElement.Lookup(doc,Id(f.Name)).Id).ToHashSet();
            foreach(var id in existing.Definition.GetFieldOrder().ToList())if(hidden.Contains(existing.Definition.GetField(id).ParameterId))existing.Definition.RemoveField(id);return existing;
        }
        var schedule=ViewSchedule.CreateSchedule(doc,new ElementId(BuiltInCategory.OST_GenericModel));schedule.Name=name;ScheduleField? layer=null;
        foreach(var f in Fields.Where(f=>f.Name is not ("CBIM_중복기반절토" or "CBIM_지층내성토" or "CBIM_중복기록"))){var field=schedule.Definition.AddField(ScheduleFieldType.Instance,SharedParameterElement.Lookup(doc,Id(f.Name)).Id);field.ColumnHeading=f.Name.Replace("CBIM_","");if(f.Type==SpecTypeId.Volume){field.ColumnHeading+=" (m³)";field.DisplayType=ScheduleFieldDisplayType.Totals;field.SetFormatOptions(new FormatOptions(UnitTypeId.CubicMeters){Accuracy=.001,UseDigitGrouping=true});}if(f.Name=="CBIM_중복지층")layer=field;}
        schedule.Definition.AddFilter(new ScheduleFilter(layer!.FieldId,ScheduleFilterType.NotEqual,""));schedule.Definition.AddSortGroupField(new(layer.FieldId,ScheduleSortOrder.Ascending));schedule.Definition.IsItemized=false;schedule.Definition.ShowGrandTotal=true;schedule.Definition.ShowGrandTotalTitle=true;schedule.Definition.GrandTotalTitle="합계";return schedule;
    }
}
