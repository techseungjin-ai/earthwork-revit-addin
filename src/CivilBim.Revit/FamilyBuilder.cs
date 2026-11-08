using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using CivilBim.Core;

namespace CivilBim.Revit;

public sealed record FamilyChoice(string Id,string Label)
{
    public override string ToString()=>Label;
}
public static class FamilyBuilder
{
    // Call after the placement/resize transaction commits, inside the caller's
    // TransactionGroup so a failed physical-geometry check rolls the operation back.
    public static int FinalizeVertical(Document doc,IEnumerable<FamilyInstance> instances,Settings settings)
    {
        var targets=instances.DistinctBy(p=>p.Id).ToList();var affected=targets.ToDictionary(p=>p.Id);
        foreach(var target in targets)
            foreach(var id in JoinGeometryUtils.GetJoinedElements(doc,target))
                if(doc.GetElement(id) is FamilyInstance other&&Storage.Info(other)?.Kind is "CIP" or "HPile")affected.TryAdd(id,other);
        int released;
        using(var transaction=new Transaction(doc,"수직부재 확정 후 자동결합 해제"))
        {
            transaction.Start();released=ReleaseVerticalJoins(doc,targets);
            foreach(var pile in affected.Values)Track(doc,pile,Storage.Info(pile)??throw new InvalidDataException("수직부재 정보가 없습니다."));
            if(transaction.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("수직부재 결합 해제를 취소했습니다.");
        }
        foreach(var pile in targets)
        {
            var info=Storage.Info(pile)!;var bounds=Bounds(pile);
            if(Math.Abs(bounds.Min.Z*.3048+settings.Frame.Elevation-info.Bottom)>.002||Math.Abs(bounds.Max.Z*.3048+settings.Frame.Elevation-info.Top)>.002)
                throw new InvalidDataException($"부재 {pile.Id} ({pile.Symbol.FamilyName}): 확정 후 실제 EL {bounds.Max.Z*.3048+settings.Frame.Elevation:F3} ~ {bounds.Min.Z*.3048+settings.Frame.Elevation:F3}m, 요청 {info.Top:F3} ~ {info.Bottom:F3}m. 겹친 부재 또는 패밀리 형상을 확인하세요.");
        }
        return released;
    }
    public static int ReleaseVerticalJoins(Document doc,IEnumerable<FamilyInstance> instances)
    {
        var count=0;
        foreach(var instance in instances)
            foreach(var id in JoinGeometryUtils.GetJoinedElements(doc,instance).ToList())
            {
                var other=doc.GetElement(id);if(other is not FamilyInstance family)continue;
                var kind=Storage.Info(family)?.Kind;var name=family.Symbol.FamilyName+" "+family.Symbol.Name;
                var knownPile=kind is "CIP" or "HPile"||name.Contains("CIP",StringComparison.OrdinalIgnoreCase)||name.Contains("C.I.P",StringComparison.OrdinalIgnoreCase)||name.Contains("PILE",StringComparison.OrdinalIgnoreCase)||name.Contains("파일",StringComparison.OrdinalIgnoreCase);
                if(!knownPile)continue;
                JoinGeometryUtils.UnjoinGeometry(doc,instance,other);count++;
            }
        doc.Regenerate();return count;
    }
    public static FamilyChoice[] Choices(Document doc)=>new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
        .OrderBy(s=>s.FamilyName).ThenBy(s=>s.Name).Select(s=>new FamilyChoice(s.UniqueId,$"{s.Category?.Name} | {s.FamilyName}: {s.Name} | {s.Family.FamilyPlacementType}")).ToArray();
    public static FamilySymbol Symbol(Document doc,string id)
    {
        var s=doc.GetElement(id) as FamilySymbol??throw new InvalidDataException("사용할 패밀리 유형을 먼저 선택하세요.");
        if(!s.IsActive){s.Activate();doc.Regenerate();}return s;
    }
    private static Level Level(Document doc,double z)=>new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l=>Math.Abs(l.Elevation-z)).FirstOrDefault()??throw new InvalidDataException("배치 레벨이 필요합니다.");
    private static void SetLength(FamilyInstance instance,string name,double meters,bool required)
    {
        if(string.IsNullOrWhiteSpace(name)){if(required)throw new InvalidDataException("인스턴스 길이 파라미터를 지정하세요.");return;}
        var matches=instance.Parameters.Cast<Parameter>().Where(p=>p.Definition.Name==name).ToList();
        if(matches.Count>1)throw new InvalidDataException($"동명 파라미터 {name}이 여러 개 있습니다.");
        var p=matches.SingleOrDefault();
        if(p==null)
        {
            p=instance.Symbol.LookupParameter(name);
            if(p!=null && p.StorageType==StorageType.Double && p.Definition.GetDataType()==SpecTypeId.Length && Math.Abs(p.AsDouble()-ModelBuilder.Feet(meters))<1e-5)return;
            throw new InvalidDataException($"{instance.Symbol.FamilyName}: {name} 인스턴스 길이 파라미터가 필요합니다. 유형값은 요청 치수와 일치할 때만 사용할 수 있습니다.");
        }
        if(p.StorageType!=StorageType.Double || p.Definition.GetDataType()!=SpecTypeId.Length)throw new InvalidDataException($"{name}은 길이 파라미터가 아닙니다.");
        if(p.IsReadOnly){if(Math.Abs(p.AsDouble()-ModelBuilder.Feet(meters))<1e-5)return;throw new InvalidDataException($"{name}은 읽기 전용이며 요청 치수와 다릅니다.");}
        if(!p.Set(ModelBuilder.Feet(meters)))throw new InvalidDataException($"{name} 설정 실패.");
    }
    public static FamilyInstance Vertical(Document doc,string symbolId,XYZ center,double top,double bottom,Settings settings,string lengthParameter,string diameterParameter="",double? diameter=null,string topParameter="")
    {
        var symbol=Symbol(doc,symbolId);var zBottom=ModelBuilder.Feet(bottom-settings.Frame.Elevation);var zTop=ModelBuilder.Feet(top-settings.Frame.Elevation);
        var level=Level(doc,zBottom);FamilyInstance instance;
        if(symbol.Family.FamilyPlacementType==FamilyPlacementType.TwoLevelsBased && symbol.Category.Id.Value==(long)BuiltInCategory.OST_StructuralColumns)
        {
            instance=doc.Create.NewFamilyInstance(new XYZ(center.X,center.Y,zBottom),symbol,level,StructuralType.Column);
            instance.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM).Set(level.Id);
            instance.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM).Set(level.Id);
            instance.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM).Set(zBottom-level.Elevation);
            instance.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM).Set(zTop-level.Elevation);
        }
        else if(symbol.Family.FamilyPlacementType==FamilyPlacementType.OneLevelBased)
        {
            instance=doc.Create.NewFamilyInstance(new XYZ(center.X,center.Y,topParameter.Length>0?0:settings.PileOriginAtTop?zTop:zBottom),symbol,level,StructuralType.NonStructural);
            SetLength(instance,lengthParameter,top-bottom,true);
            if(topParameter.Length>0)SetLength(instance,topParameter,top-settings.Frame.Elevation,true);
        }
        else throw new InvalidDataException($"수직 부재 패밀리 {symbol.FamilyName}의 배치 방식 {symbol.Family.FamilyPlacementType}은 아직 지원하지 않습니다. 비호스트 단일 레벨 또는 구조 기둥을 선택하세요.");
        if(diameter.HasValue)SetLength(instance,diameterParameter,diameter.Value,!string.IsNullOrWhiteSpace(diameterParameter));
        doc.Regenerate();
        var box=Bounds(instance);
        if(Math.Abs((box.Max.Z-box.Min.Z)-(zTop-zBottom))>ModelBuilder.Feet(.02))
            throw new InvalidDataException($"{symbol.FamilyName}: 실제 높이 {(box.Max.Z-box.Min.Z)*.3048:F4}m와 요청 {top-bottom:F4}m가 다릅니다. 길이 파라미터/수식을 확인하세요.");
        // 비호스트 패밀리도 레벨/원점의 처리 방식이 다르므로 실제 솔리드 하단을 기준으로 정렬한다.
        if(Math.Abs(box.Min.Z-zBottom)>1e-6){ElementTransformUtils.MoveElement(doc,instance.Id,new XYZ(0,0,zBottom-box.Min.Z));doc.Regenerate();box=Bounds(instance);}
        if(Math.Abs(box.Min.Z-zBottom)>ModelBuilder.Feet(.002) || Math.Abs(box.Max.Z-zTop)>ModelBuilder.Feet(.002))
            throw new InvalidDataException($"{symbol.FamilyName}: 요청 EL 정렬 실패. 실제 {box.Min.Z*.3048:F3}~{box.Max.Z*.3048:F3}, 요청 {zBottom*.3048:F3}~{zTop*.3048:F3}.");
        return instance;
    }
    public static bool IsPointGeneric(FamilySymbol symbol)=>
        symbol.Category.Id.Value==(long)BuiltInCategory.OST_GenericModel &&
        symbol.Family.FamilyPlacementType==FamilyPlacementType.OneLevelBased;

    public static FamilyInstance PointGeneric(Document doc,string symbolId,XYZ point,double angle)
    {
        var symbol=Symbol(doc,symbolId);
        if(!IsPointGeneric(symbol))throw new InvalidDataException("호스트가 없는 단일 레벨 점 기반 일반 모델을 선택하세요.");
        var instance=doc.Create.NewFamilyInstance(point,symbol,Level(doc,point.Z),StructuralType.NonStructural);
        doc.Regenerate();
        if(instance.Location is not LocationPoint location)throw new InvalidDataException("패밀리 삽입점을 읽을 수 없습니다.");
        var delta=point-location.Point;
        if(delta.GetLength()>1e-7)ElementTransformUtils.MoveElement(doc,instance.Id,delta);
        var rotation=angle-location.Rotation;
        if(Math.Abs(rotation)>1e-7)ElementTransformUtils.RotateElement(doc,instance.Id,Line.CreateBound(point,point+XYZ.BasisZ),rotation);
        doc.Regenerate();
        if(instance.Location is not LocationPoint placed||placed.Point.DistanceTo(point)>ModelBuilder.Feet(.001))
            throw new InvalidDataException("패밀리 삽입점이 요청 위치와 다릅니다.");
        return instance;
    }

    public static FamilyInstance Horizontal(Document doc,string symbolId,XYZ start,XYZ end,bool alignCenter=false)
    {
        var symbol=Symbol(doc,symbolId);var level=Level(doc,start.Z);
        if(symbol.Family.FamilyPlacementType is not (FamilyPlacementType.CurveBased or FamilyPlacementType.CurveDrivenStructural))
            throw new InvalidDataException($"{symbol.FamilyName}: 선 기반 또는 구조 프레임 패밀리를 선택하세요.");
        var type=symbol.Family.FamilyPlacementType==FamilyPlacementType.CurveDrivenStructural?StructuralType.Beam:StructuralType.NonStructural;
        var instance=doc.Create.NewFamilyInstance(Line.CreateBound(start,end),symbol,level,type);
        Parameter? hostOffset=null;
        if(type==StructuralType.Beam){StructuralFramingUtils.DisallowJoinAtEnd(instance,0);StructuralFramingUtils.DisallowJoinAtEnd(instance,1);}
        else
        {
            hostOffset=instance.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM)??instance.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
            if(hostOffset is {IsReadOnly:false,StorageType:StorageType.Double})hostOffset.Set(start.Z-level.Elevation);
        }
        doc.Regenerate();
        // Generic line families keep LocationCurve on the host plane; their offset belongs to the physical placement line.
        XYZ Effective(XYZ p)=>hostOffset is {StorageType:StorageType.Double}?p+XYZ.BasisZ.Multiply(hostOffset.AsDouble()):p;
        if(instance.Location is LocationCurve initial)
        {
            var a=Effective(initial.Curve.GetEndPoint(0));var b=Effective(initial.Curve.GetEndPoint(1));var desired=end-start;
            if(Math.Abs(a.DistanceTo(b)-desired.GetLength())<ModelBuilder.Feet(.002)&&Math.Abs((b-a).Normalize().DotProduct(desired.Normalize()))>.999999)
            {
                // Some generic line families interpret input Z relative to the selected level.
                var delta=(b-a).DotProduct(desired)>=0?start-a:start-b;
                if(delta.GetLength()>1e-6){ElementTransformUtils.MoveElement(doc,instance.Id,delta);doc.Regenerate();}
            }
        }
        if(instance.Location is not LocationCurve location || Math.Min(Effective(location.Curve.GetEndPoint(0)).DistanceTo(start)+Effective(location.Curve.GetEndPoint(1)).DistanceTo(end),Effective(location.Curve.GetEndPoint(1)).DistanceTo(start)+Effective(location.Curve.GetEndPoint(0)).DistanceTo(end))>ModelBuilder.Feet(.002))
            throw new InvalidDataException("수평 패밀리의 배치 곡선이 요청 중심선과 다릅니다. "+(instance.Location is LocationCurve lc?$"실제 {lc.Curve.GetEndPoint(0)}~{lc.Curve.GetEndPoint(1)} / 요청 {start}~{end}":"Location="+instance.Location?.GetType().Name)+"\n"+string.Join(";",instance.Parameters.Cast<Parameter>().Where(p=>p.StorageType==StorageType.Double&&p.Definition.GetDataType()==SpecTypeId.Length).Select(p=>$"{p.Definition.Name}({p.Id.Value})={p.AsDouble():F6},readOnly={p.IsReadOnly}")));
        if(alignCenter&&Math.Abs(start.Z-end.Z)<ModelBuilder.Feet(.002))
        {
            var bounds=Bounds(instance);var delta=start.Z-(bounds.Min.Z+bounds.Max.Z)/2;
            if(Math.Abs(delta)>ModelBuilder.Feet(.002))
            {
                // RFA origins/beam justification may be at the top or bottom of the section.
                // Keep the drawn plan curve, compensate the physical section's centre height.
                var offset=type==StructuralType.Beam?instance.get_Parameter(BuiltInParameter.Z_OFFSET_VALUE):hostOffset;
                if(offset is {IsReadOnly:false,StorageType:StorageType.Double})offset.Set(offset.AsDouble()+delta);
                else ElementTransformUtils.MoveElement(doc,instance.Id,XYZ.BasisZ.Multiply(delta));
                doc.Regenerate();bounds=Bounds(instance);
                if(Math.Abs((bounds.Min.Z+bounds.Max.Z)/2-start.Z)>ModelBuilder.Feet(.002))throw new InvalidDataException($"{symbol.FamilyName}: 패밀리 기준점 간격을 보정한 뒤에도 실제 단면 중심 높이가 다릅니다. 패밀리 높이/간격 수식을 확인하세요.");
            }
        }
        return instance;
    }
    public static FamilyInstance Wall(Document doc,string symbolId,XYZ start,XYZ end,double height,string heightParameter)
    {
        var instance=Horizontal(doc,symbolId,start,end);SetLength(instance,heightParameter,height,true);return instance;
    }
    public static void Resize(Document doc,FamilyInstance instance,ElementInfo info,double top,double bottom,Settings settings)
    {
        if(top<=bottom)throw new InvalidDataException(info.ParentId+": 상하단 EL이 역전되었습니다.");
        var low=ModelBuilder.Feet(bottom-settings.Frame.Elevation);var high=ModelBuilder.Feet(top-settings.Frame.Elevation);
        if(instance.Symbol.Family.FamilyPlacementType==FamilyPlacementType.TwoLevelsBased)
        {
            var baseLevel=(Level)doc.GetElement(instance.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM).AsElementId());
            var topLevel=(Level)doc.GetElement(instance.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM).AsElementId());
            instance.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM).Set(low-baseLevel.Elevation);instance.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM).Set(high-topLevel.Elevation);
        }
        else
        {
            SetLength(instance,info.LengthParameter,top-bottom,true);if(info.TopParameter.Length>0)SetLength(instance,info.TopParameter,top-settings.Frame.Elevation,true);
            doc.Regenerate();var bounds=Bounds(instance);
            if(Math.Abs((bounds.Max.Z-bounds.Min.Z)-(high-low))>ModelBuilder.Feet(.02))throw new InvalidDataException(info.ParentId+": 패밀리 길이 수식을 확인하세요.");
            ElementTransformUtils.MoveElement(doc,instance.Id,new XYZ(0,0,low-bounds.Min.Z));
        }
        doc.Regenerate();var final=Bounds(instance);if(Math.Abs(final.Min.Z-low)>ModelBuilder.Feet(.002)||Math.Abs(final.Max.Z-high)>ModelBuilder.Feet(.002))throw new InvalidDataException(info.ParentId+": 깊이 변경 후 실제 상하단 EL이 요청값과 다릅니다.");
        Track(doc,instance,info with {Top=top,Bottom=bottom,Length=top-bottom});
    }
    public static IEnumerable<Solid> Solids(GeometryElement geometry)
    {
        foreach(var g in geometry)
            if(g is Solid s && s.Volume>0)yield return s;
            else if(g is GeometryInstance i)foreach(var solid in Solids(i.GetInstanceGeometry()))yield return solid;
    }
    public static BoundingBoxXYZ Bounds(Element element)
    {
        var corners=Solids(element.get_Geometry(new Options {DetailLevel=ViewDetailLevel.Fine})).SelectMany(s=>
        {
            var b=s.GetBoundingBox();return from x in new[]{b.Min.X,b.Max.X} from y in new[]{b.Min.Y,b.Max.Y} from z in new[]{b.Min.Z,b.Max.Z} select b.Transform.OfPoint(new XYZ(x,y,z));
        }).ToList();
        if(corners.Count==0)throw new InvalidDataException($"부재 {element.Id} ({(element as FamilyInstance)?.Symbol.FamilyName}): 상세 수준 ‘높음’에서 실제 3D 솔리드가 없습니다. 패밀리 형상·가시성 또는 삭제된 형상을 확인하세요.");
        return new BoundingBoxXYZ {Min=new(corners.Min(p=>p.X),corners.Min(p=>p.Y),corners.Min(p=>p.Z)),Max=new(corners.Max(p=>p.X),corners.Max(p=>p.Y),corners.Max(p=>p.Z))};
    }
    public static FamilyInstance Track(Document doc,FamilyInstance instance,ElementInfo info)
    {
        doc.Regenerate();var box=Bounds(instance);
        var solids=Solids(instance.get_Geometry(new Options {DetailLevel=ViewDetailLevel.Fine})).ToList();if(solids.Count==0)throw new InvalidDataException("물량을 읽을 수 있는 패밀리 솔리드가 없습니다.");
        info=info with {LocalMin=ModelBuilder.Meters(box.Min),LocalMax=ModelBuilder.Meters(box.Max),Quantity=solids.Sum(ModelBuilder.Volume),FamilySymbol=instance.Symbol.UniqueId};
        Storage.Write(instance,info);
        var comments=instance.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        if(comments is {IsReadOnly:false})comments.Set($"CivilBIM | {info.ParentId} | {info.Layer} | EL {info.Top:F3}~{info.Bottom:F3} | {info.Boreholes}");
        return instance;
    }
}
