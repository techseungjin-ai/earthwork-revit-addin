using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CivilBim.Core;
using Microsoft.Win32;

namespace CivilBim.Revit;

public sealed class ReviewHole
{
    public string Id {get;set;}="";public double Easting {get;set;}public double Northing {get;set;}public double GroundElevation {get;set;}
}
public sealed class ReviewLayer
{
    public string Hole {get;set;}="";public string Name {get;set;}="";public double BottomDepth {get;set;}
}
public sealed class ReviewWindow:ModelessWindow
{
    public string? SavedPath {get;private set;}
    public ObservableCollection<ReviewHole> Holes {get;}=[];
    public ObservableCollection<ReviewLayer> Layers {get;}=[];
    private readonly BoreholeSet source;
    private readonly DataGrid holeGrid,layerGrid;
    private readonly CheckBox reviewed=new(){Content="PDF 원문과 공번·좌표·지표 EL·지층명·하단 심도 및 경고를 확인했습니다.",Margin=new(0,10,0,10)};
    public ReviewWindow(BoreholeSet data)
    {
        source=data;Title="시추 PDF 추출표 · 확인 후 적용";Width=1000;Height=770;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        foreach(var h in data.Boreholes){Holes.Add(new(){Id=h.Id,Easting=h.Easting,Northing=h.Northing,GroundElevation=h.GroundElevation});foreach(var l in h.Layers)Layers.Add(new(){Hole=h.Id,Name=l.Name,BottomDepth=l.BottomDepth});}
        var dock=new DockPanel {Margin=new(20)};Content=dock;
        var top=new StackPanel();DockPanel.SetDock(top,Dock.Top);dock.Children.Add(top);
        top.Children.Add(new TextBlock {Text="시추공 위치와 지층을 표에서 확인·수정합니다",FontSize=22,FontWeight=FontWeights.SemiBold});
        top.Children.Add(new TextBlock {Text="좌표는 시추공의 위치이며 지표 EL은 공의 지표 표고입니다. 지층 하단 심도는 해당 공 지표면부터 아래로 잰 누적 깊이(m)입니다.",TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,10)});
        if(data.ReviewNotes.Count>0)top.Children.Add(new ScrollViewer {MaxHeight=130,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=new TextBlock {Text=string.Join("\n",data.ReviewNotes),TextWrapping=TextWrapping.Wrap,Foreground=System.Windows.Media.Brushes.DarkRed}});
        var bottom=new StackPanel();DockPanel.SetDock(bottom,Dock.Bottom);dock.Children.Add(bottom);bottom.Children.Add(reviewed);
        var buttons=new StackPanel {Orientation=Orientation.Horizontal};bottom.Children.Add(buttons);
        var apply=new Button {Content="검토 완료 · 적용",Padding=new(16,9,16,9)};buttons.Children.Add(apply);
        var save=new Button {Content="JSON 파일로 별도 저장",Padding=new(12,9,12,9),Margin=new(10,0,0,0)};buttons.Children.Add(save);
        var tabs=new TabControl {Margin=new(0,12,0,0)};dock.Children.Add(tabs);
        holeGrid=Grid(Holes);holeGrid.Columns.Add(Column("공번","Id"));holeGrid.Columns.Add(Column("E 좌표 (m)","Easting"));holeGrid.Columns.Add(Column("N 좌표 (m)","Northing"));holeGrid.Columns.Add(Column("지표 EL (m)","GroundElevation"));tabs.Items.Add(new TabItem {Header="1. 시추공 위치 / 지표 표고",Content=holeGrid});
        layerGrid=Grid(Layers);layerGrid.Columns.Add(Column("공번","Hole"));layerGrid.Columns.Add(Column("지층명","Name"));layerGrid.Columns.Add(Column("누적 하단 심도 (m)","BottomDepth"));tabs.Items.Add(new TabItem {Header="2. 공별 지층 / 하단 심도",Content=layerGrid});
        apply.Click+=(_,_)=>{try{var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CivilBIM","Boreholes");Directory.CreateDirectory(folder);Save(Path.Combine(folder,"boreholes-"+Guid.NewGuid().ToString("N")+".json"));Accept();}catch(Exception ex){SetStatus(ex.Message,true);}};
        save.Click+=(_,_)=>{try{BuildReviewed();var d=new SaveFileDialog {Filter="시추자료 JSON|*.json",FileName="boreholes.reviewed.json"};if(d.ShowDialog(this)==true){Save(d.FileName);Accept();}}catch(Exception ex){SetStatus(ex.Message,true);}};
    }
    private static DataGrid Grid(object items)=>new(){ItemsSource=(System.Collections.IEnumerable)items,AutoGenerateColumns=false,CanUserAddRows=true,CanUserDeleteRows=true,SelectionMode=DataGridSelectionMode.Extended,FontSize=14,RowHeight=30,ColumnHeaderHeight=34,HorizontalGridLinesBrush=System.Windows.Media.Brushes.LightGray,VerticalGridLinesBrush=System.Windows.Media.Brushes.LightGray};
    private static DataGridTextColumn Column(string title,string property)=>new(){Header=title,Binding=new System.Windows.Data.Binding(property){UpdateSourceTrigger=System.Windows.Data.UpdateSourceTrigger.LostFocus},MinWidth=180,Width=new DataGridLength(1,DataGridLengthUnitType.Star)};
    public BoreholeSet BuildReviewed(bool? confirmed=null)
    {
        if(!holeGrid.CommitEdit(DataGridEditingUnit.Cell,true)||!holeGrid.CommitEdit(DataGridEditingUnit.Row,true)||!layerGrid.CommitEdit(DataGridEditingUnit.Cell,true)||!layerGrid.CommitEdit(DataGridEditingUnit.Row,true))throw new InvalidDataException("표에 입력한 숫자를 확인하세요.");
        if(!(confirmed??reviewed.IsChecked==true))throw new InvalidDataException("원문 대조 확인란을 체크한 뒤 적용하세요.");
        if(Layers.Any(l=>Holes.All(h=>h.Id!=l.Hole)))throw new InvalidDataException("지층 표의 공번이 시추공 위치 표와 다릅니다.");
        var result=new BoreholeSet {Crs=source.Crs,Source=source.Source,ReviewNotes=source.ReviewNotes.ToList(),Reviewed=true,Boreholes=Holes.Select(h=>new Borehole(h.Id.Trim(),h.Easting,h.Northing,h.GroundElevation,Layers.Where(l=>l.Hole==h.Id).Select(l=>new Stratum(l.Name.Trim(),l.BottomDepth)).ToList())).ToList()};result.Validate(result.Crs);return result;
    }
    public void Save(string file){File.WriteAllText(file,JsonSerializer.Serialize(BuildReviewed(),BoreholeSet.JsonOptions));SavedPath=file;SetStatus("검토 자료를 저장했습니다: "+file);}
}
