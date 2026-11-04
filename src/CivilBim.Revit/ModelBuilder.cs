using Autodesk.Revit.DB;
using CivilBim.Core;

namespace CivilBim.Revit;

public static class ModelBuilder
{
    public static double Feet(double m)=>m/.3048;
    public static XYZ Xyz(Point3 p)=>new(Feet(p.X),Feet(p.Y),Feet(p.Z));
    public static Point3 Meters(XYZ p)=>new(p.X*.3048,p.Y*.3048,p.Z*.3048);
    public static double Volume(Solid solid)=>solid.Volume*Math.Pow(.3048,3);
    public static CurveLoop Loop(IList<XYZ> points)
    {
        var loop=new CurveLoop();for(var i=0;i<points.Count;i++)loop.Append(Line.CreateBound(points[i],points[(i+1)%points.Count]));return loop;
    }
    private static ElementId Material(Document doc,string layer)
    {
        var name="CivilBIM · "+layer;
        var found=new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Material)).Cast<Autodesk.Revit.DB.Material>().FirstOrDefault(x=>x.Name==name);
        if(found!=null)return found.Id;
        var id=Autodesk.Revit.DB.Material.Create(doc,name);var m=(Autodesk.Revit.DB.Material)doc.GetElement(id);
        m.Color=layer switch {"매립토층"=>new(218,185,120),"퇴적토층"=>new(184,171,102),"풍화토층"=>new(189,128,61),"풍화암층"=>new(153,95,62),"연암층"=>new(122,138,151),"보통암층"=>new(82,106,123),_=>new(140,150,160)};
        return id;
    }
    public static Solid Cylinder(XYZ center,double bottomLocal,double topLocal,double diameter,ElementId material)
    {
        var p=new XYZ(center.X,center.Y,Feet(bottomLocal));var r=Feet(diameter/2);var loop=new CurveLoop();
        loop.Append(Arc.Create(p,r,0,Math.PI,XYZ.BasisX,XYZ.BasisY));loop.Append(Arc.Create(p,r,Math.PI,Math.PI*2,XYZ.BasisX,XYZ.BasisY));
        return GeometryCreationUtilities.CreateExtrusionGeometry(new[]{loop},XYZ.BasisZ,Feet(topLocal-bottomLocal),new SolidOptions(material,ElementId.InvalidElementId));
    }
    public static FamilyInstance Pile(Document doc,XYZ center,PileSegment segment,Settings settings,string source,string batch,string? familyOverride=null)
    {
        var instance=FamilyBuilder.Vertical(doc,familyOverride??settings.CipFamily,center,segment.TopElevation,segment.BottomElevation,settings,settings.CipLengthParameter,topParameter:settings.CipTopParameter);
        var actualVolume=FamilyBuilder.Solids(instance.get_Geometry(new Options {DetailLevel=ViewDetailLevel.Fine})).Sum(Volume);
        // Internal equivalent diameter is metadata only; never write or validate type dimensions.
        var diameter=Math.Sqrt(4*actualVolume/(Math.PI*segment.Length));
        var position=settings.Frame.ToSurvey(Meters(center));
        var info=new ElementInfo("CIP",segment.PileId,segment.Layer,segment.TopElevation,segment.BottomElevation,diameter,actualVolume,segment.Length,
            segment.BoreholeIds,source,batch,position,settings.Frame,settings.Scale) {LengthParameter=settings.CipLengthParameter,DiameterParameter="",OriginAtTop=settings.PileOriginAtTop,TopParameter=settings.CipTopParameter};
        // 지층 색은 뷰 오버라이드로만 표시하여 원본 패밀리 재료를 보존한다.
        var material=Material(doc,segment.Layer);var color=((Autodesk.Revit.DB.Material)doc.GetElement(material)).Color;
        var fill=new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().FirstOrDefault(f=>f.GetFillPattern().IsSolidFill);
        if(doc.ActiveView!=null && !doc.ActiveView.IsTemplate)
        {
            var style=new OverrideGraphicSettings().SetProjectionLineColor(color).SetSurfaceForegroundPatternColor(color).SetCutForegroundPatternColor(color);
            if(fill!=null)style.SetSurfaceForegroundPatternId(fill.Id).SetCutForegroundPatternId(fill.Id);
            doc.ActiveView.SetElementOverrides(instance.Id,style);
        }
        return FamilyBuilder.Track(doc,instance,info);
    }
    public static DirectShape Shape(Document doc,Solid solid,ElementInfo info)
    {
        var bounds=solid.GetBoundingBox();
        var corners=(from x in new[]{bounds.Min.X,bounds.Max.X} from y in new[]{bounds.Min.Y,bounds.Max.Y} from z in new[]{bounds.Min.Z,bounds.Max.Z} select bounds.Transform.OfPoint(new XYZ(x,y,z))).ToList();
        info=info with {LocalMin=Meters(new(corners.Min(p=>p.X),corners.Min(p=>p.Y),corners.Min(p=>p.Z))),LocalMax=Meters(new(corners.Max(p=>p.X),corners.Max(p=>p.Y),corners.Max(p=>p.Z)))};
        var shape=DirectShape.CreateElement(doc,new ElementId(BuiltInCategory.OST_GenericModel));shape.ApplicationId="CivilBIM";shape.ApplicationDataId=info.ParentId+":"+info.Layer;
        shape.Name=$"{info.Kind} · {info.ParentId} · {info.Layer}";shape.SetShape(new GeometryObject[]{solid});Storage.Write(shape,info);
        shape.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set($"{info.Kind} | {info.ParentId} | {info.Layer} | EL {info.Top:F3}~{info.Bottom:F3} | {info.Length:F3}m | {info.Quantity:F4}m³ | {info.Boreholes}");
        return shape;
    }
    public static Solid HSection(XYZ start,XYZ end,Settings settings,ElementId material)
    {
        var width=Feet(settings.SteelWidth);var height=Feet(settings.SteelHeight);var tf=Feet(settings.Flange);var tw=Feet(settings.Web);
        if(width<=0 || height<=0 || tf<=0 || tw<=0 || tf*2>=height || tw>=width) throw new InvalidDataException("H형강 단면 치수 확인 필요.");
        var axis=(end-start).Normalize();var vertical=Math.Abs(axis.Z)>.99;
        var u=vertical?XYZ.BasisX:XYZ.BasisZ.CrossProduct(axis).Normalize();var v=axis.CrossProduct(u).Normalize();
        var coords=new (double X,double Y)[]{(-width/2,-height/2),(width/2,-height/2),(width/2,-height/2+tf),(tw/2,-height/2+tf),
            (tw/2,height/2-tf),(width/2,height/2-tf),(width/2,height/2),(-width/2,height/2),(-width/2,height/2-tf),(-tw/2,height/2-tf),(-tw/2,-height/2+tf),(-width/2,-height/2+tf)};
        var loop=Loop(coords.Select(p=>start+u.Multiply(p.X)+v.Multiply(p.Y)).ToList());
        return GeometryCreationUtilities.CreateExtrusionGeometry(new[]{loop},axis,start.DistanceTo(end),new SolidOptions(material,ElementId.InvalidElementId));
    }
    public static ElementInfo Info(string kind,string id,string layer,Solid solid,XYZ start,XYZ end,Settings s,string source,string batch)=>
        new(kind,id,layer,Math.Max(start.Z,end.Z)*.3048+s.Frame.Elevation,Math.Min(start.Z,end.Z)*.3048+s.Frame.Elevation,0,Volume(solid),start.DistanceTo(end)*.3048,"",source,batch,s.Frame.ToSurvey(Meters(start)),s.Frame,s.Scale);
    public static ElementId Steel(Document doc)=>Material(doc,"형강");
    public static ElementId Timber(Document doc)=>Material(doc,"토류벽");
    public static void VerifyUnchanged(Element element,ElementInfo info)
    {
        var box=FamilyBuilder.Bounds(element);
        if(info.LocalMin is not Point3 min || info.LocalMax is not Point3 max) throw new InvalidDataException("형상 검증 정보가 없습니다. 가시설을 재생성하세요.");
        var a=Meters(box.Min);var b=Meters(box.Max);
        double Difference(Point3 p,Point3 q)=>Math.Max(Math.Max(Math.Abs(p.X-q.X),Math.Abs(p.Y-q.Y)),Math.Abs(p.Z-q.Z));
        if(Difference(a,min)>.002 || Difference(b,max)>.002)throw new InvalidDataException($"{info.ParentId}: 생성 후 이동/변경된 모델입니다. 원본 CAD에서 가시설을 재생성하세요.");
        var solids=FamilyBuilder.Solids(element.get_Geometry(new Options {DetailLevel=ViewDetailLevel.Fine})).ToList();
        if(Math.Abs(solids.Sum(Volume)-info.Quantity)>Math.Max(.00001,info.Quantity*.001))throw new InvalidDataException($"{info.ParentId}: 형상 체적과 기록값이 다릅니다.");
    }
}
