using Autodesk.Revit.DB;
using CivilBim.Core;
using NetTopologySuite.Geometries;
using Geometry=NetTopologySuite.Geometries.Geometry;

namespace CivilBim.Revit;

public sealed record SoilResult(List<ElementId> Elements,ViewSchedule Schedule,View3D View,List<SoilBody> Bodies);
public static class SoilBuilder
{
    public static SoilResult Generate(Document doc,Toposolid terrain,Geometry boundary,BoreholeSet boreholes,Settings settings,double excavationEl,double radius,bool calculateExcavation=true,bool limitRadius=true,TerrainMesh? surface=null)
    {
        var bodies=SiteGeology.Build(surface??ContextBuilder.Surface(terrain),boundary,boreholes,settings.Frame,excavationEl,radius,calculateExcavation,limitRadius);
        return Create(doc,boundary,bodies,settings,settings.Frame,excavationEl,calculateExcavation);
    }
    public static SoilResult GenerateStandalone(Document doc,BoreholeSet data,Settings settings,Geometry? boundary=null)
    {
        var frame=SiteGeology.BoreholeFrame(data,settings.Frame,settings.BoreholeFrame);
        var scope=boundary??SiteGeology.BoreholeScope(data,frame);
        var bodies=SiteGeology.BuildFromBoreholes(data,frame,scope);
        var result=Create(doc,scope,bodies,settings,frame,0,false);
        settings.BoreholeFrame=frame;return result;
    }
    private static SoilResult Create(Document doc,Geometry boundary,List<SoilBody> bodies,Settings settings,SiteFrame frame,double excavationEl,bool calculateExcavation)
    {
        if(bodies.Count==0)throw new InvalidDataException("생성할 시추 지층이 없습니다.");
        using var t=new Transaction(doc,"작업대지 지층 / 굴착 물량");t.Start();QuantityParameters.Ensure(doc);
        if(!calculateExcavation)settings.BoreholeFrame=frame;
        if(calculateExcavation)settings.ExcavationElevation=excavationEl;settings.HasExcavationElevation=calculateExcavation;settings.WorksiteBoundaryWkt=boundary.AsText();Storage.Write(doc.ProjectInformation,settings);
        var previous=Storage.Generated(doc).Where(x=>x.Info.Kind=="SiteSoil").Select(x=>x.Element.Id).ToList();if(previous.Count>0)doc.Delete(previous);
        var ids=new List<ElementId>();var batch=Guid.NewGuid().ToString();
        foreach(var body in bodies)
        {
            var material=SoilMaterial(doc,body.Layer);var solids=new List<Solid>();
            foreach(var wedge in body.Wedges)
            {
                try { solids.Add(Wedge(wedge,material)); }
                catch(Exception ex) { throw new InvalidDataException($"{body.Layer} 지층 조각 생성 실패 (X {wedge.A.X:F3}, Y {wedge.A.Y:F3}m / 체적 {wedge.Volume:G6}m³). 기존 지층은 보존됩니다. {ex.Message}",ex); }
            }
            var nativeVolume=solids.Sum(ModelBuilder.Volume);
            if(Math.Abs(nativeVolume-body.Volume)>Math.Max(.001,body.Volume*.00001))throw new InvalidDataException($"{body.Layer} 지층 솔리드/계산 체적 불일치.");
            var element=DirectShape.CreateElement(doc,new ElementId(BuiltInCategory.OST_GenericModel));element.Name="작업대지 지층 · "+body.Layer;element.SetShape(solids.Cast<GeometryObject>().ToList());
            Storage.Write(element,new ElementInfo("SiteSoil",body.Layer,body.Layer,body.Wedges.Max(w=>Math.Max(w.A.Z,Math.Max(w.B.Z,w.C.Z)))+frame.Elevation,body.Wedges.Min(w=>w.Bottom)+frame.Elevation,0,nativeVolume,0,string.Join(";",body.Boreholes),"WORKSITE",batch,new(),frame,settings.Scale));
            QuantityParameters.Set(element,body.Layer,string.Join(";",body.Boreholes),nativeVolume,body.ExcavatedArea,body.ExcavatedVolume,calculateExcavation);ids.Add(element.Id);
        }
        var points=boundary.Coordinates.Select(c=>new Point3(c.X,c.Y,excavationEl-frame.Elevation)).ToList();
        var view=Views.Create(doc,"02_작업대지 지층",points);
        view.DisplayStyle=DisplayStyle.Shading;view.IsSectionBoxActive=true;
        foreach(var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            if(element is ImportInstance || element is Toposolid || Storage.Info(element)?.Kind=="Building")if(element.CanBeHidden(view))view.HideElements(new[]{element.Id});
        var box=view.GetSectionBox();box.Min=new(box.Min.X,box.Min.Y,ModelBuilder.Feet(bodies.Min(b=>b.Wedges.Min(w=>w.Bottom))-1));box.Max=new(box.Max.X,box.Max.Y,ModelBuilder.Feet(bodies.Max(b=>b.Wedges.Max(w=>Math.Max(w.A.Z,Math.Max(w.B.Z,w.C.Z))))+2));view.SetSectionBox(box);
        var fill=new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().FirstOrDefault(p=>p.GetFillPattern().IsSolidFill);
        if(fill!=null)foreach(var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v=>!v.IsTemplate&&v.AreGraphicsOverridesAllowed()))
        foreach(var id in ids)
        {
            var layer=Storage.Info(doc.GetElement(id))!.Layer;var color=((Material)doc.GetElement(SoilMaterial(doc,layer))).Color;
            var graphics=v.GetElementOverrides(id).SetSurfaceForegroundPatternId(fill.Id).SetSurfaceForegroundPatternColor(color).SetSurfaceForegroundPatternVisible(true).SetCutForegroundPatternId(fill.Id).SetCutForegroundPatternColor(color).SetCutForegroundPatternVisible(true);
            v.SetElementOverrides(id,graphics);
        }
        var schedule=QuantityParameters.Schedule(doc,calculateExcavation);t.Commit();return new(ids,schedule,view,bodies);
    }
    public static Solid Wedge(SoilWedge wedge,ElementId material)
    {
        try {var solid=MaterialWedge(wedge,material);CheckVolume(solid,wedge);return solid;}
        catch { /* Keep the validated analytic fallbacks for degenerate kernels. */ }
        // Build an analytic prism and trim its top plane. Zero-height corners at a
        // stratum/terrain intersection do not require collapsed tessellated faces.
        try
        {
            var points=new[]{wedge.A,wedge.B,wedge.C}.Select(ModelBuilder.Xyz).ToArray();
            var z=ModelBuilder.Feet(wedge.Bottom);
            var loop=new CurveLoop();
            for(var i=0;i<3;i++)loop.Append(Line.CreateBound(new(points[i].X,points[i].Y,z),new(points[(i+1)%3].X,points[(i+1)%3].Y,z)));
            var solid=GeometryCreationUtilities.CreateExtrusionGeometry(new[]{loop},XYZ.BasisZ,points.Max(p=>p.Z)-z,new SolidOptions(material,ElementId.InvalidElementId));
            if(points.Max(p=>p.Z)-points.Min(p=>p.Z)>1e-9)
            {
                var normal=(points[1]-points[0]).CrossProduct(points[2]-points[0]).Normalize();
                if(normal.Z>0)normal=-normal;
                solid=BooleanOperationsUtils.CutWithHalfSpace(solid,Plane.CreateByNormalAndOrigin(normal,points[0]));
            }
            CheckVolume(solid,wedge);return solid;
        }
        catch(Exception analytic)
        {
            try { var solid=ClippedBoxWedge(wedge,material);CheckVolume(solid,wedge);return solid; }
            catch(Exception clipped)
            {
                try { var solid=TessellatedWedge(wedge,material);CheckVolume(solid,wedge);return solid; }
                catch(Exception mesh) { throw new InvalidDataException("솔리드 생성/체적 검증 실패: "+analytic.Message+" / "+clipped.Message+" / "+mesh.Message,mesh); }
            }
        }
    }
    private static Solid MaterialWedge(SoilWedge wedge,ElementId material)
    {
        var top=new[]{ModelBuilder.Xyz(wedge.A),ModelBuilder.Xyz(wedge.B),ModelBuilder.Xyz(wedge.C)};
        var bottom=top.Select(p=>new XYZ(p.X,p.Y,ModelBuilder.Feet(wedge.Bottom))).ToArray();
        if((top[1]-top[0]).CrossProduct(top[2]-top[0]).Z<0){(top[1],top[2])=(top[2],top[1]);(bottom[1],bottom[2])=(bottom[2],bottom[1]);}
        using var builder=new BRepBuilder(BRepType.Solid);builder.SetAllowShortEdges();
        var edges=new Dictionary<string,(BRepBuilderGeometryId Id,XYZ Start)>();
        static string Key(XYZ p)=>FormattableString.Invariant($"{p.X:R},{p.Y:R},{p.Z:R}");
        void Face(params XYZ[] vertices)
        {
            var p=new List<XYZ>();foreach(var v in vertices)if(p.All(q=>q.DistanceTo(v)>1e-9))p.Add(v);if(p.Count<3)return;
            var normal=(p[1]-p[0]).CrossProduct(p[2]-p[0]);if(normal.GetLength()<1e-15)return;
            var face=builder.AddFace(BRepBuilderSurfaceGeometry.Create(Plane.CreateByNormalAndOrigin(normal.Normalize(),p[0]),null),false);builder.SetFaceMaterialId(face,material);
            var loop=builder.AddLoop(face);
            for(var i=0;i<p.Count;i++)
            {
                var a=p[i];var b=p[(i+1)%p.Count];var ka=Key(a);var kb=Key(b);var key=string.CompareOrdinal(ka,kb)<0?ka+"|"+kb:kb+"|"+ka;
                if(!edges.TryGetValue(key,out var edge)){edge=(builder.AddEdge(BRepBuilderEdgeGeometry.Create(a,b)),a);edges[key]=edge;}
                builder.AddCoEdge(loop,edge.Id,edge.Start.DistanceTo(a)>1e-9);
            }
            builder.FinishLoop(loop);builder.FinishFace(face);
        }
        Face(top);Face(bottom[2],bottom[1],bottom[0]);for(var i=0;i<3;i++){var j=(i+1)%3;Face(bottom[i],bottom[j],top[j],top[i]);}
        if(builder.Finish()!=BRepBuilderOutcome.Success)throw new InvalidDataException("지층 면 재질 솔리드 생성 실패");return builder.GetResult();
    }
    private static Solid ClippedBoxWedge(SoilWedge wedge,ElementId material)
    {
        // Clipping a roomy box avoids creating sub-ShortCurveTolerance triangle edges.
        // No vertices are dropped, merged or nudged to inflate a narrow wedge.
        var p=new[]{ModelBuilder.Xyz(wedge.A),ModelBuilder.Xyz(wedge.B),ModelBuilder.Xyz(wedge.C)};
        var z=ModelBuilder.Feet(wedge.Bottom);var x0=p.Min(v=>v.X)-1;var x1=p.Max(v=>v.X)+1;var y0=p.Min(v=>v.Y)-1;var y1=p.Max(v=>v.Y)+1;
        var corners=new[]{new XYZ(x0,y0,z),new XYZ(x1,y0,z),new XYZ(x1,y1,z),new XYZ(x0,y1,z)};var loop=new CurveLoop();
        for(var i=0;i<4;i++)loop.Append(Line.CreateBound(corners[i],corners[(i+1)%4]));
        var solid=GeometryCreationUtilities.CreateExtrusionGeometry(new[]{loop},XYZ.BasisZ,Math.Max(1,p.Max(v=>v.Z)-z+1),new SolidOptions(material,ElementId.InvalidElementId));
        for(var i=0;i<3;i++)
        {
            var a=p[i];var b=p[(i+1)%3];var other=p[(i+2)%3];var normal=new XYZ(-(b.Y-a.Y),b.X-a.X,0).Normalize();if(normal.DotProduct(other-a)<0)normal=-normal;
            solid=BooleanOperationsUtils.CutWithHalfSpace(solid,Plane.CreateByNormalAndOrigin(normal,a));
        }
        var top=(p[1]-p[0]).CrossProduct(p[2]-p[0]).Normalize();if(top.Z>0)top=-top;
        return BooleanOperationsUtils.CutWithHalfSpace(solid,Plane.CreateByNormalAndOrigin(top,p[0]));
    }
    private static void CheckVolume(Solid solid,SoilWedge wedge)
    {
        if(solid.Volume<=0||Math.Abs(ModelBuilder.Volume(solid)-wedge.Volume)>Math.Max(1e-8,wedge.Volume*1e-5))throw new InvalidDataException("지층 조각의 해석 체적이 원본과 다릅니다.");
    }
    private static Solid TessellatedWedge(SoilWedge wedge,ElementId material)
    {
        var top=new[]{ModelBuilder.Xyz(wedge.A),ModelBuilder.Xyz(wedge.B),ModelBuilder.Xyz(wedge.C)};
        var bottom=new[]{new XYZ(top[0].X,top[0].Y,ModelBuilder.Feet(wedge.Bottom)),new XYZ(top[1].X,top[1].Y,ModelBuilder.Feet(wedge.Bottom)),new XYZ(top[2].X,top[2].Y,ModelBuilder.Feet(wedge.Bottom))};
        if((top[1]-top[0]).CrossProduct(top[2]-top[0]).Z<0){(top[1],top[2])=(top[2],top[1]);(bottom[1],bottom[2])=(bottom[2],bottom[1]);}
        var builder=new TessellatedShapeBuilder {Target=TessellatedShapeBuilderTarget.Solid,Fallback=TessellatedShapeBuilderFallback.Abort};builder.OpenConnectedFaceSet(true);
        void Face(params XYZ[] vertices)
        {
            var clean=new List<XYZ>();foreach(var p in vertices)if(clean.All(q=>p.DistanceTo(q)>1e-7))clean.Add(p);
            if(clean.Count>=3)builder.AddFace(new TessellatedFace(clean,material));
        }
        Face(top);Face(bottom[2],bottom[1],bottom[0]);
        for(var i=0;i<3;i++){var j=(i+1)%3;Face(bottom[i],bottom[j],top[j],top[i]);}
        builder.CloseConnectedFaceSet();builder.Build();return builder.GetBuildResult().GetGeometricalObjects().OfType<Solid>().Single();
    }
    private static ElementId SoilMaterial(Document doc,string layer)
    {
        var name="CivilBIM 지층 · "+layer;var existing=new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(m=>m.Name==name);if(existing!=null)return existing.Id;
        var id=Material.Create(doc,name);var material=(Material)doc.GetElement(id);
        material.Color=layer.Contains("매립")?new(213,179,105):layer.Contains("퇴적")?new(190,156,82):layer.Contains("풍화토")?new(181,124,77):layer.Contains("풍화암")?new(148,94,72):layer.Contains("연암")?new(116,134,156):new(78,99,126);return id;
    }
}
public static class QuantityParameters
{
    private static readonly (string Name,ForgeTypeId Type)[] Fields=[("CBIM_지층",SpecTypeId.String.Text),("CBIM_공번",SpecTypeId.String.Text),("CBIM_전체체적",SpecTypeId.Volume),("CBIM_굴착면적",SpecTypeId.Area),("CBIM_굴착체적",SpecTypeId.Volume),("CBIM_수량상태",SpecTypeId.String.Text)];
    private static Guid Id(string name)=>new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("CivilBIM-quantities-v2:"+name)));
    public static void Ensure(Document doc)
    {
        if(Fields.All(f=>SharedParameterElement.Lookup(doc,Id(f.Name))!=null))return;
        var app=doc.Application;var old=app.SharedParametersFilename;var file=Path.Combine(Path.GetTempPath(),"CivilBIM-quantities-"+Guid.NewGuid().ToString("N")+".txt");
        File.WriteAllText(file,"# Revit shared parameter file.\n*META\tVERSION\tMINVERSION\nMETA\t2\t1\n*GROUP\tID\tNAME\n*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE\n");
        try
        {
            app.SharedParametersFilename=file;var definitions=app.OpenSharedParameterFile()??throw new InvalidDataException("물량 파라미터 정의 파일 생성 실패.");var group=definitions.Groups.Create("CivilBIM");
            var categories=app.Create.NewCategorySet();categories.Insert(doc.Settings.Categories.get_Item(BuiltInCategory.OST_GenericModel));
            foreach(var f in Fields)
            {
                if(SharedParameterElement.Lookup(doc,Id(f.Name))!=null)continue;
                var definition=group.Definitions.Create(new ExternalDefinitionCreationOptions(f.Name,f.Type){GUID=Id(f.Name),UserModifiable=false});
                if(!doc.ParameterBindings.Insert(definition,app.Create.NewInstanceBinding(categories),GroupTypeId.Data))throw new InvalidDataException(f.Name+" 물량 파라미터 바인딩 실패.");
            }
        }finally{app.SharedParametersFilename=old;}
    }
    public static void Set(Element e,string layer,string holes,double volume,double area,double excavation,bool calculated=true)
    {
        e.get_Parameter(Id("CBIM_지층")).Set(layer);e.get_Parameter(Id("CBIM_공번")).Set(holes);
        e.get_Parameter(Id("CBIM_전체체적")).Set(UnitUtils.ConvertToInternalUnits(volume,UnitTypeId.CubicMeters));
        e.get_Parameter(Id("CBIM_굴착면적")).Set(UnitUtils.ConvertToInternalUnits(area,UnitTypeId.SquareMeters));
        e.get_Parameter(Id("CBIM_굴착체적")).Set(UnitUtils.ConvertToInternalUnits(excavation,UnitTypeId.CubicMeters));
        e.get_Parameter(Id("CBIM_수량상태")).Set(calculated?"수평 바닥 EL 참고계산":"지층 전체체적만 계산 · 굴착량은 중복체적 명령에서 산정");
    }
    public static void MarkStale(Element e)=>e.get_Parameter(Id("CBIM_수량상태"))?.Set("지형 수정 후 재계산 필요");
    public static ViewSchedule Schedule(Document doc,bool includeExcavation=true)
    {
        var name=includeExcavation?"03_작업대지 지층별 굴착 물량":"03_작업대지 지층 전체체적";
        var existing=new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().FirstOrDefault(s=>s.Name==name);
        if(existing!=null)return existing;
        var schedule=ViewSchedule.CreateSchedule(doc,new ElementId(BuiltInCategory.OST_GenericModel));schedule.Name=name;
        ScheduleField? layerField=null;
        foreach(var f in Fields)
        {
            if(!includeExcavation&&f.Name.StartsWith("CBIM_굴착"))continue;
            var parameter=SharedParameterElement.Lookup(doc,Id(f.Name));var field=schedule.Definition.AddField(ScheduleFieldType.Instance,parameter.Id);field.ColumnHeading=f.Name.Replace("CBIM_","");
            if(f.Type==SpecTypeId.Volume || f.Type==SpecTypeId.Area)
            {field.SetFormatOptions(new FormatOptions(f.Type==SpecTypeId.Area?UnitTypeId.SquareMeters:UnitTypeId.CubicMeters){Accuracy=.01,UseDigitGrouping=true});if(f.Type==SpecTypeId.Volume)field.DisplayType=ScheduleFieldDisplayType.Totals;field.ColumnHeading+=f.Type==SpecTypeId.Area?" (m²)":" (m³)";}
            if(f.Name=="CBIM_지층")layerField=field;
        }
        schedule.Definition.AddFilter(new ScheduleFilter(layerField!.FieldId,ScheduleFilterType.NotEqual,""));schedule.Definition.AddSortGroupField(new(layerField.FieldId,ScheduleSortOrder.Ascending));
        schedule.Definition.IsItemized=false;schedule.Definition.ShowGrandTotal=true;schedule.Definition.ShowGrandTotalTitle=true;schedule.Definition.GrandTotalTitle="합계";return schedule;
    }
}
