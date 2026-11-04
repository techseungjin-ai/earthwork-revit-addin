using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CivilBim.Core;

namespace CivilBim.Revit;

public sealed class DepthReviewWindow : ModelessWindow
{
    public double TopElevation {get;private set;}
    public double Bottom {get;private set;}
    public bool SoftRock {get;private set;}
    public double? Diameter {get;private set;}
    public DepthReviewWindow(List<DepthCandidate> candidates,double top,double bottom)
    {
        Title="단면/전개도 깊이 후보 · 적용 범위 검토";Width=1050;Height=700;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var dock=new DockPanel {Margin=new(15)};Content=dock;
        var header=new TextBlock {Text="도면에서 읽은 EL·길이 후보입니다. 적용할 단면을 선택하고 상하단 EL을 확인하세요. 이 값은 다음 생성하는 CIP/H-Pile에 공통 적용됩니다. 서로 다른 깊이 구역은 별도 CAD 영역으로 나누어 생성하세요.",TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,12)};
        DockPanel.SetDock(header,Dock.Top);dock.Children.Add(header);
        var panel=new StackPanel {Orientation=Orientation.Horizontal,Margin=new(0,10,0,0)};DockPanel.SetDock(panel,Dock.Bottom);dock.Children.Add(panel);
        panel.Children.Add(new TextBlock {Text="상단 EL (m)",VerticalAlignment=VerticalAlignment.Center});var topBox=new TextBox {Text=top.ToString(CultureInfo.InvariantCulture),Width=100,Margin=new(8),Padding=new(6)};panel.Children.Add(topBox);
        panel.Children.Add(new TextBlock {Text="하단 EL (m)",VerticalAlignment=VerticalAlignment.Center});var bottomBox=new TextBox {Text=bottom.ToString(CultureInfo.InvariantCulture),Width=100,Margin=new(8),Padding=new(6)};panel.Children.Add(bottomBox);
        var use=new Button {Content="선택 후보 값 가져오기",Padding=new(8),Margin=new(8)};panel.Children.Add(use);
        var setBottom=new Button {Content="선택 EL → 하단",Padding=new(8),Margin=new(8)};panel.Children.Add(setBottom);
        var apply=new Button {Content="확인 값 적용",Padding=new(8),Margin=new(8)};panel.Children.Add(apply);
        var grid=new DataGrid {ItemsSource=candidates,AutoGenerateColumns=true,IsReadOnly=true,SelectionMode=DataGridSelectionMode.Single};dock.Children.Add(grid);
        use.Click+=(_,_)=>
        {
            if(grid.SelectedItem is not DepthCandidate c)return;
            if(c.TopElevation.HasValue)topBox.Text=c.TopElevation.Value.ToString(CultureInfo.InvariantCulture);
            else if(c.ObservedElevation.HasValue)topBox.Text=c.ObservedElevation.Value.ToString(CultureInfo.InvariantCulture);
            if(c.BottomElevation.HasValue)bottomBox.Text=c.BottomElevation.Value.ToString(CultureInfo.InvariantCulture);
            else if(c.Length.HasValue && c.TopElevation==null)bottomBox.Text=(Guard.Number(topBox.Text)-c.Length.Value).ToString(CultureInfo.InvariantCulture);
            SoftRock=c.BottomAtSoftRockTop;Diameter=c.Diameter;
            if(SoftRock)SetStatus($"CIP 선단 = 최근접 시추공 연암층 상단. 직경 후보 {Diameter}m. 시추자료를 확인하세요.");
        };
        setBottom.Click+=(_,_)=>{if(grid.SelectedItem is DepthCandidate {ObservedElevation:double el})bottomBox.Text=el.ToString(CultureInfo.InvariantCulture);};
        apply.Click+=(_,_)=>
        {
            try {TopElevation=Guard.Number(topBox.Text);Bottom=Guard.Number(bottomBox.Text);Guard.Finite(TopElevation,Bottom);if(TopElevation<=Bottom)throw new InvalidDataException("상단은 하단보다 높아야 합니다.");Accept();}
            catch(Exception ex){SetStatus(ex.Message,true);}
        };
    }
}
