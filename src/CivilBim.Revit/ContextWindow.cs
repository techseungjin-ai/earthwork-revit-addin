using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using CivilBim.Core;
using Microsoft.Win32;

namespace CivilBim.Revit;

public sealed class ContextWindow : ModelessWindow
{
    public Settings Settings {get;}
    public ContextWindow(Document doc,Settings s)
    {
        Settings=s;Title="CivilBIM · SHP 지형 / 도로 / 주변건물";Width=720;Height=880;MinHeight=650;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new StackPanel {Margin=new(24)};Content=new ScrollViewer {Content=root,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        root.Children.Add(new TextBlock {Text="SHP 폴더로 대지 작성",FontSize=25,FontWeight=FontWeights.SemiBold,Margin=new(0,0,0,8)});
        root.Children.Add(new TextBlock {Text="하위 도엽 폴더를 함께 읽어 지형·도로 구획·건물 매스를 작성합니다.",Margin=new(0,0,0,18)});
        TextBox Text(string label,string value){root.Children.Add(new TextBlock {Text=label,Margin=new(0,8,0,4)});var t=new TextBox {Text=value,Padding=new(7)};root.Children.Add(t);return t;}
        ComboBox Choice(string label,FamilyChoice[] choices,string id)
        {root.Children.Add(new TextBlock {Text=label,Margin=new(0,8,0,4)});var c=new ComboBox {ItemsSource=choices,MinHeight=32};c.SelectedItem=choices.FirstOrDefault(x=>x.Id==id)??choices.FirstOrDefault();root.Children.Add(c);return c;}
        var folder=Text("수치지도 SHP 묶음 폴더",s.SiteFolder);
        var browse=new Button {Content="폴더 찾기",HorizontalAlignment=HorizontalAlignment.Right,Padding=new(10,5,10,5)};root.Children.Add(browse);
        browse.Click+=(_,_)=>{var d=new OpenFolderDialog {Title="SHP 도엽 폴더들을 포함한 상위 폴더"};if(d.ShowDialog(this)==true)folder.Text=d.FolderName;};
        var types=new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).Cast<ToposolidType>().OrderBy(t=>t.Name).Select(t=>new FamilyChoice(t.UniqueId,t.Name)).ToArray();
        var terrain=Choice("지형 유형 (Toposolid)",types,s.TerrainType);var road=Choice("도로 구획 유형 (Toposolid)",types,s.RoadType);
        var materials=new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().OrderBy(m=>m.Name).Select(m=>new FamilyChoice(m.UniqueId,m.Name)).ToArray();
        var material=Choice("주변 건물 매스 재질",materials,s.BuildingMaterial);
        var scale=Text("원본 축척 분모 (1000 / 2500 / 5000 / 10000 등)",s.Scale.ToString(CultureInfo.InvariantCulture));
        var offset=Text("도로 구획 돌출 높이 (mm)",(s.RoadOffset*1000).ToString(CultureInfo.InvariantCulture));
        var rotation=Text("대지 회전각 (°) · 평면 위에서 +반시계 / -시계",s.Frame.RotationDegrees.ToString(CultureInfo.InvariantCulture));rotation.Name="SiteRotation";
        var pivotText=new TextBlock {TextWrapping=TextWrapping.Wrap,Margin=new(0,6,0,0)};root.Children.Add(pivotText);
        void ShowPivot()=>pivotText.Text=s.SiteRotationPivot is Point3 p?$"회전 중심: Revit X={p.X:F3}, Y={p.Y:F3}m (자동 원점 기준)":"회전 중심: SHP 통합 범위 중심 / 생성 모델 내부 원점";ShowPivot();
        var align=new Button {Content="Revit 북방향 두 선 → 생성 회전각 · 중심점 설정",Padding=new(9),Margin=new(0,7,0,0)};root.Children.Add(align);align.Click+=(_,_)=>Api(app=>ModelessHost.Open(app,"대지생성방향",()=>new SiteAlignmentWindow((angle,pivot)=>{if(WasClosed)throw new InvalidDataException("SHP 생성 창을 다시 여세요.");rotation.Text=angle.ToString("G17",CultureInfo.InvariantCulture);s.SiteRotationPivot=pivot;ShowPivot();SetStatus("회전각/중심점 설정 완료. 지형·도로·주변 건물 생성 버튼을 누르세요.");})));
        var resetPivot=new Button {Content="회전 중심을 SHP 통합 중심으로 초기화",Padding=new(6),Margin=new(0,5,0,0)};root.Children.Add(resetPivot);resetPivot.Click+=(_,_)=>{s.SiteRotationPivot=null;ShowPivot();};
        root.Children.Add(new TextBlock {Text="선에서 구한 각도와 중심점은 생성 버튼을 누를 때 지형·도로·건물 및 시추 좌표에 함께 적용합니다. SHP의 자동 중심을 내부 원점에 놓은 상태에서 선택한 Revit 중심점을 기준으로 회전합니다. 기존 대지 회전은 리본의 대지 방향 기능을 사용하세요.",TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,0)});
        root.Children.Add(new TextBlock {Text="예: 대지 방향을 시계로 30° 돌리려면 -30. 진북/도북·공유좌표 설정은 변경하지 않습니다. 생성 후 Revit 기본 이동/회전으로 따로 바꾼 위치는 자동 추적하지 않으므로 시추공 위치를 확인하세요.",TextWrapping=TextWrapping.Wrap,Margin=new(0,7,0,0)});
        root.Children.Add(new TextBlock {Text="좌표 원점과 표고 필드는 자동으로 찾습니다. 지도 축척으로 좌표를 확대하지 않습니다.",TextWrapping=TextWrapping.Wrap,Margin=new(0,16,0,12)});
        var reduction=Text("지형점 감소율 (%) · 0=전체 유지 / 예:30=약30% 감소",s.TerrainReductionPercent.ToString(CultureInfo.InvariantCulture));
        root.Children.Add(new TextBlock {Text="전체 점으로 지형을 먼저 생성합니다. 감소율이 0보다 크면 Revit 지형 솔리드 단순화를 한 번 적용합니다. 형상 보존을 위해 실제 감소율은 요청과 다를 수 있으며, 많은 점은 생성 시간이 길어질 수 있습니다.",TextWrapping=TextWrapping.Wrap,Margin=new(0,6,0,8)});
        var create=new Button {Content="지형 · 도로 · 주변 건물 생성",Padding=new(14,10,14,10)};root.Children.Add(create);
        create.Click+=(_,_)=>
        {
            try
            {
                if(!Directory.Exists(folder.Text))throw new InvalidDataException("SHP 상위 폴더를 선택하세요.");
                s.SiteFolder=folder.Text.Trim();s.TerrainType=((FamilyChoice?)terrain.SelectedItem)?.Id??throw new InvalidDataException("지형 유형을 선택하세요.");
                s.RoadType=((FamilyChoice?)road.SelectedItem)?.Id??throw new InvalidDataException("도로 유형을 선택하세요.");s.BuildingMaterial=((FamilyChoice?)material.SelectedItem)?.Id??throw new InvalidDataException("건물 재질을 선택하세요.");
                s.Scale=int.Parse(scale.Text,CultureInfo.InvariantCulture);s.RoadOffset=Guard.Number(offset.Text)/1000;
                var angle=Guard.Number(rotation.Text);Guard.Finite(angle);s.Frame=s.Frame with {RotationDegrees=angle};
                if(s.Scale<1 || s.RoadOffset<=0)throw new InvalidDataException("축척 분모와 도로 높이는 양수여야 합니다.");
                s.Sampling=s.Scale<=1000?2:s.Scale<=5000?5:10;
                s.TerrainReductionPercent=Guard.Number(reduction.Text);Guard.Finite(s.TerrainReductionPercent);if(s.TerrainReductionPercent<0||s.TerrainReductionPercent>=100)throw new InvalidDataException("지형점 감소율은 0 이상 100 미만이어야 합니다.");Accept();
            }catch(Exception ex){SetStatus(ex.Message,true);}
        };
        StickyActions(create);
    }
}
