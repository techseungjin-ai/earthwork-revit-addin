using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CivilBim.Core;
using NetTopologySuite.Geometries.Utilities;
using System.Windows;
using System.Windows.Controls;

namespace CivilBim.Revit;

public static class SiteAlignment
{
    public static int Rotate(Document doc,Point3 pivot,double degrees,ElementId? sourceLine=null)
    {
        var settings=Storage.Load(doc);var generated=Storage.Generated(doc);
        var terrains=generated.Where(x=>x.Info.Kind=="Terrain").ToList();if(terrains.Count!=1)throw new InvalidDataException("기존 대지 회전은 CivilBIM 지형이 한 개 필요합니다. 생성 전에는 SHP 생성 창의 북방향 두 선 설정을 사용하세요.");settings.Frame.Validate();
        var next=settings.Frame.RotateAbout(pivot,degrees);var radians=degrees*Math.PI/180;
        var rotation2d=AffineTransformation.RotationInstance(radians,pivot.X,pivot.Y);
        NetTopologySuite.Geometries.Geometry Footprint(Toposolid topo)=>NetTopologySuite.Operation.Union.UnaryUnionOp.Union(ContextBuilder.Surface(topo).Triangles.Select(t=>(NetTopologySuite.Geometries.Geometry)t.Polygon).ToList());
        var roadFootprints=generated.Where(x=>x.Info.Kind=="Road"&&x.Element is Toposolid).Select(x=>(Road:(Toposolid)x.Element,Old:Footprint((Toposolid)x.Element))).ToList();
        var roots=generated.Where(x=>x.Element is not Toposolid topo||topo.HostTopoId==ElementId.InvalidElementId).Select(x=>x.Element.Id).ToList();
        if(sourceLine!=null&&doc.GetElement(sourceLine) is CurveElement or ReferencePlane)roots.Add(sourceLine);
        using var transaction=new Transaction(doc,"북방향 선으로 대지와 시추 좌표 함께 회전");transaction.Start();
        ElementTransformUtils.RotateElements(doc,roots,Line.CreateBound(ModelBuilder.Xyz(pivot with {Z=0}),ModelBuilder.Xyz(pivot with {Z=1})),radians);doc.Regenerate();
        foreach(var (road,old) in roadFootprints)
        {
            var expected=rotation2d.Transform(old);var actual=Footprint(road);var tolerance=Math.Max(.01,old.Area*.00001);
            if(expected.SymmetricDifference(actual).Area<=tolerance)continue;
            if(old.SymmetricDifference(actual).Area<=tolerance)
            {ElementTransformUtils.RotateElement(doc,road.Id,Line.CreateBound(ModelBuilder.Xyz(pivot with {Z=0}),ModelBuilder.Xyz(pivot with {Z=1})),radians);doc.Regenerate();actual=Footprint(road);}
            if(expected.SymmetricDifference(actual).Area>tolerance)throw new InvalidDataException("도로 구획이 원본 지형과 함께 회전하지 못했습니다. 전체 회전을 되돌렸습니다.");
        }
        foreach(var (element,info) in generated)
        {
            var updated=info with {Frame=next,Rotation=info.Rotation+radians};
            if(element is FamilyInstance family)FamilyBuilder.Track(doc,family,updated);
            else if(element is DirectShape){var bounds=FamilyBuilder.Bounds(element);Storage.Write(element,updated with {LocalMin=ModelBuilder.Meters(bounds.Min),LocalMax=ModelBuilder.Meters(bounds.Max)});}
            else Storage.Write(element,updated);
        }
        var transform=AffineTransformation.RotationInstance(radians,pivot.X,pivot.Y);
        if(settings.WorksiteBoundaryWkt.Length>0){var boundary=new NetTopologySuite.IO.WKTReader(Polygons.Factory.GeometryServices).Read(settings.WorksiteBoundaryWkt);settings.WorksiteBoundaryWkt=transform.Transform(boundary).AsText();}
        settings.PlanMin=null;settings.PlanMax=null;settings.Frame=next;Storage.Write(doc.ProjectInformation,settings);
        // Section boxes must follow rotated site data, or the view can appear empty.
        foreach(var view in new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Where(v=>!v.IsTemplate&&v.IsSectionBoxActive&&(v.Name.StartsWith("01_대지·도로·주변건물")||v.Name.StartsWith("02_작업대지 지층")||v.Name.StartsWith("시추공 좌표 위치 검토"))))
        {
            var box=view.GetSectionBox();var rotation=Transform.CreateRotationAtPoint(XYZ.BasisZ,radians,ModelBuilder.Xyz(pivot with {Z=0}));box.Transform=rotation.Multiply(box.Transform);view.SetSectionBox(box);
        }
        if(transaction.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("대지 회전이 취소되었습니다.");return roots.Count;
    }
}
public sealed class SiteAlignmentWindow:ModelessWindow
{
    public SiteAlignmentWindow(Action<double,Point3>? forCreation=null)
    {
        Title="대지 방향 · 북방향 선으로 맞춤";Width=740;Height=580;MinHeight=480;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new StackPanel {Margin=new(22)};Content=new ScrollViewer {Content=root,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        root.Children.Add(new TextBlock {Text=forCreation==null?"생성된 SHP 대지 → 굴착 도면 방향으로 회전":"선에서 구한 회전값 → SHP 생성 창에 전달",FontSize=21,TextWrapping=TextWrapping.Wrap});
        root.Children.Add(new TextBlock {Text="현황/SHP 기준 북방향 선과 굴착 도면의 북방향 선을 순서대로 선택하세요. 선은 남쪽에서 북쪽으로 그립니다. 선택한 첫 선의 시작점을 기본 회전 중심으로 사용합니다. CAD 링크는 회전하지 않습니다.",TextWrapping=TextWrapping.Wrap,Margin=new(0,12,0,10)});
        List<Point3>? source=null,target=null;Point3? pivot=null;ElementId? sourceLine=null;
        var state=new TextBlock {TextWrapping=TextWrapping.Wrap,Margin=new(0,12,0,12)};
        var reverseSource=new CheckBox {Content="현황/SHP 북방향 뒤집기",Margin=new(0,6,0,6)};var reverseTarget=new CheckBox {Content="굴착 도면 북방향 뒤집기",Margin=new(0,6,0,6)};
        void Show(){try{state.Text=$"현황/SHP 선 {(source==null?"미선택":"선택됨")} / 굴착 선 {(target==null?"미선택":"선택됨")}\n"+(source!=null&&target!=null?$"회전 {DrawingSelections.NorthAngle(source,target,reverseSource.IsChecked==true,reverseTarget.IsChecked==true):F3}° · +반시계/-시계":"")+(pivot is Point3 p?$"\n회전 중심 X {p.X:F3}, Y {p.Y:F3}m":"");}catch(Exception ex){SetStatus(ex.Message,true);}}
        Button Button(string text,Action<UIApplication> action){var button=new Button {Content=text,Padding=new(10),Margin=new(0,5,0,5)};root.Children.Add(button);button.Click+=(_,_)=>Api(action,true);return button;}
        Button("1. 현재 대지(SHP) Revit 북방향 선 선택",app=>{source=DrawingSelections.RevitPath(app.ActiveUIDocument,"현재 대지의 Revit 참조선/참조평면/모델선을 선택하세요.",out var id);sourceLine=id;pivot=source[0];Show();});root.Children.Add(reverseSource);
        Button("2. 굴착 도면 Revit 북방향 선 선택",app=>{target=DrawingSelections.RevitPath(app.ActiveUIDocument,"목표 북방향 Revit 참조선/참조평면/모델선을 선택하세요.",out _);Show();});root.Children.Add(reverseTarget);
        Button("회전 중심점 별도 지정",app=>{pivot=ModelBuilder.Meters(app.ActiveUIDocument.Selection.PickPoint("대지를 회전할 중심점을 지정하세요."));Show();});root.Children.Add(state);
        reverseSource.Click+=(_,_)=>Show();reverseTarget.Click+=(_,_)=>Show();
        var apply=Button(forCreation==null?"선택한 선 기준으로 대지 회전 적용":"회전각 · 중심점을 SHP 생성 창에 적용",app=>
        {
            if(source==null||target==null||pivot==null)throw new InvalidDataException("두 북방향 선과 회전 중심을 지정하세요.");
            var angle=DrawingSelections.NorthAngle(source,target,reverseSource.IsChecked==true,reverseTarget.IsChecked==true);
            if(forCreation!=null){forCreation(angle,pivot.Value);SetStatus("회전각과 중심점을 SHP 생성 창에 전달했습니다. SHP 창의 생성 버튼을 누르세요. 아직 모델은 변경하지 않았습니다.");return;}
            var count=SiteAlignment.Rotate(app.ActiveUIDocument.Document,pivot.Value,angle,sourceLine);
            ModelessHost.CloseOthers(app.ActiveUIDocument.Document,this);source=null;target=null;Show();SetStatus($"{angle:F3}° 회전 적용 완료 ({count}개). 시추 좌표·작업경계·기존 CivilBIM 부재도 함께 갱신했습니다. 다른 기능창은 최신 좌표로 다시 여세요.");
        });StickyActions(apply);
    }
}
[Transaction(TransactionMode.Manual)]
public sealed class SiteAlignmentCommand:IExternalCommand
{
    public Result Execute(ExternalCommandData data,ref string message,ElementSet elements){if(data.Application.ActiveUIDocument==null)return Result.Cancelled;ModelessHost.Open(data.Application,"대지방향",()=>new SiteAlignmentWindow());return Result.Succeeded;}
}
