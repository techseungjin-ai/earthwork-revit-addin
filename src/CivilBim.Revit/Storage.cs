using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using CivilBim.Core;

namespace CivilBim.Revit;

public sealed class Settings
{
    public string SiteFolder {get;set;}="";
    public string TerrainType {get;set;}="";
    public string RoadType {get;set;}="";
    public string BuildingMaterial {get;set;}="";
    public double RoadOffset {get;set;}=.15;
    public double BuildingFloorHeight {get;set;}=3.3;
    public SiteFrame Frame {get;set;}=new("",0,0,0,0);
    public SiteFrame? BoreholeFrame {get;set;}
    public string Shp {get;set;}="";
    public string ElevationField {get;set;}="";
    public int Scale {get;set;}=1000;
    public double Sampling {get;set;}=2;
    public Point3? SiteRotationPivot {get;set;}
    public double TerrainReductionPercent {get;set;}
    public double PileTop {get;set;}=10;
    public double PileBottom {get;set;}=-15;
    public double Diameter {get;set;}=.6;
    public double PileSpacing {get;set;}=.6;
    public double Radius {get;set;}=100;
    public string Method {get;set;}="Nearest";
    public string BoreholeJson {get;set;}="";
    public string CipLayer {get;set;}="";
    public string WalerLayer {get;set;}="";
    public string StrutLayer {get;set;}="";
    public string HPileLayer {get;set;}="";
    public string WallLayer {get;set;}="";
    public string ExcavationLayer {get;set;}="";
    public string WalerElevations {get;set;}="5,0,-5";
    public double SteelWidth {get;set;}=.3;
    public double SteelHeight {get;set;}=.3;
    public double Flange {get;set;}=.015;
    public double Web {get;set;}=.01;
    public double WallThickness {get;set;}=.1;
    public double ExcavationElevation {get;set;}=-10;
    public bool HasExcavationElevation {get;set;}
    public string WorksiteBoundaryWkt {get;set;}="";
    public bool CenterlineCip {get;set;}
    public string CipFamily {get;set;}="";
    public string HPileFamily {get;set;}="";
    public string WalerFamily {get;set;}="";
    public string StrutFamily {get;set;}="";
    public string WallFamily {get;set;}="";
    public string CipLengthParameter {get;set;}="길이";
    public string CipDiameterParameter {get;set;}="직경";
    public string HPileLengthParameter {get;set;}="길이";
    public string CipTopParameter {get;set;}="";
    public string HPileTopParameter {get;set;}="";
    public double HPileRotationOffset {get;set;}
    public string WallHeightParameter {get;set;}="높이";
    public bool PileOriginAtTop {get;set;}
    public double TerrainDepth {get;set;}=50;
    public bool CipBottomAtSoftRock {get;set;}
    public bool CoordinatesConfirmed {get;set;}
    public string CadUniqueId {get;set;}="";
    public Point3? PlanMin {get;set;}
    public Point3? PlanMax {get;set;}
}
public sealed record ElementInfo(string Kind,string ParentId,string Layer,double Top,double Bottom,double Diameter,
    double Quantity,double Length,string Boreholes,string Source,string Batch,Point3 Position,SiteFrame Frame,int Scale)
{
    public Point3? LocalMin {get;init;}
    public Point3? LocalMax {get;init;}
    public string FamilySymbol {get;init;}="";
    public string LengthParameter {get;init;}="";
    public string DiameterParameter {get;init;}="";
    public bool OriginAtTop {get;init;}
    public double Rotation {get;init;}
    public string TopParameter {get;init;}="";
}

public static class Storage
{
    private static readonly Guid SchemaId=new("eacdfab7-d08f-4d20-8bba-e8aa85801371");
    private static Schema Schema()
    {
        var s=Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaId); if(s!=null)return s;
        var b=new SchemaBuilder(SchemaId); b.SetSchemaName("CivilBimV1"); b.SetReadAccessLevel(AccessLevel.Public);b.SetWriteAccessLevel(AccessLevel.Public);
        b.AddSimpleField("Json",typeof(string));return b.Finish();
    }
    public static string? Read(Element e)
    {
        var s=Schema();var entity=e.GetEntity(s);return entity.IsValid()?entity.Get<string>(s.GetField("Json")):null;
    }
    public static void Write(Element e,object value)
    {
        var s=Schema();var entity=new Entity(s);entity.Set(s.GetField("Json"),JsonSerializer.Serialize(value));e.SetEntity(entity);
    }
    public static Settings Load(Document doc)=>Read(doc.ProjectInformation) is string s?JsonSerializer.Deserialize<Settings>(s)??new():new();
    public static ElementInfo? Info(Element e)
    {
        var s=Read(e);return s==null?null:JsonSerializer.Deserialize<ElementInfo>(s);
    }
    public static List<(Element Element,ElementInfo Info)> Generated(Document doc)=>new FilteredElementCollector(doc).WhereElementIsNotElementType()
        .Where(e=>e is DirectShape || e is FamilyInstance || e is Toposolid || e is Floor).Select(e=>(Element:e,Info:Info(e))).Where(x=>x.Info!=null).Select(x=>(x.Element,x.Info!)).ToList();
}
