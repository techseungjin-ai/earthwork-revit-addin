using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CivilBim.Core;
using Microsoft.Win32;

namespace CivilBim.Revit;

public sealed class BoreholePicker
{
    public TextBox PathBox {get;}=new(){Padding=new(7)};
    public event Action? Loaded;
    private readonly ModelessWindow owner;
    private readonly Settings settings;
    private readonly bool standalone;
    private readonly TextBlock summary=new(){TextWrapping=TextWrapping.Wrap,Margin=new(0,6,0,8)};
    public BoreholePicker(ModelessWindow window,Panel panel,Settings s,bool standalone=false)
    {
        owner=window;settings=s;this.standalone=standalone;PathBox.Text=s.BoreholeJson;
        panel.Children.Add(new TextBlock {Text="시추자료 PDF / 검토 자료",Margin=new(0,8,0,5)});panel.Children.Add(PathBox);
        var buttons=new StackPanel {Orientation=Orientation.Horizontal};panel.Children.Add(buttons);
        var browse=new Button {Content="PDF / JSON 찾기",Padding=new(10,6,10,6),Margin=new(0,5,8,0)};buttons.Children.Add(browse);
        var review=new Button {Content="추출표 검토 / 수정",Padding=new(10,6,10,6),Margin=new(0,5,0,0)};buttons.Children.Add(review);
        panel.Children.Add(new TextBlock {Text="PDF를 고르면 추출표가 열립니다. 확인 후 ‘검토 완료 · 적용’을 누르면 자료가 연결됩니다. JSON을 직접 변환할 필요는 없습니다.",TextWrapping=TextWrapping.Wrap,Margin=new(0,6,0,0)});panel.Children.Add(summary);
        browse.Click+=(_,_)=>owner.Api(app=>
        {
            var folder=File.Exists(PathBox.Text)?Path.GetDirectoryName(PathBox.Text):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads");
            var d=new OpenFileDialog {Title="시추주상도 PDF 또는 검토 자료 JSON 선택",Filter="시추자료 PDF / JSON|*.pdf;*.json|시추 PDF|*.pdf|검토 JSON|*.json",InitialDirectory=folder??""};if(d.ShowDialog(owner)!=true)return;
            var data=Read(d.FileName);if(data.Reviewed&&Path.GetExtension(d.FileName).Equals(".json",StringComparison.OrdinalIgnoreCase)){Use(d.FileName);return;}
            ModelessHost.Review(app,new(data),Use);
        });
        review.Click+=(_,_)=>owner.Api(app=>ModelessHost.Review(app,new(Read(PathBox.Text)),Use));
        PathBox.LostKeyboardFocus+=(_,_)=>{try{if(File.Exists(PathBox.Text)&&Path.GetExtension(PathBox.Text).Equals(".json",StringComparison.OrdinalIgnoreCase))UpdateSummary(Data());}catch(Exception ex){owner.SetStatus(ex.Message,true);}};
        if(File.Exists(PathBox.Text))try{UpdateSummary(Data());}catch(Exception ex){summary.Text=ex.Message;}
    }
    public BoreholeSet Read(string file)
    {
        if(!File.Exists(file))throw new InvalidDataException("시추 PDF 또는 검토 자료 파일을 먼저 선택하세요.");
        if(!standalone&&string.IsNullOrWhiteSpace(settings.Frame.Crs))throw new InvalidDataException("프로젝트의 좌표 연결 정보가 없습니다.");
        var crs=!string.IsNullOrWhiteSpace(settings.Frame.Crs)?settings.Frame.Crs:settings.BoreholeFrame?.Crs??"PDF-LOCAL";
        var data=Path.GetExtension(file).Equals(".pdf",StringComparison.OrdinalIgnoreCase)?BoreholePdf.ExtractCandidates(file,crs):JsonSerializer.Deserialize<BoreholeSet>(File.ReadAllText(file),BoreholeSet.JsonOptions)??throw new InvalidDataException("시추자료를 읽을 수 없습니다.");
        if(standalone&&string.IsNullOrWhiteSpace(data.Crs))data.Crs="PDF-LOCAL";return data;
    }
    public BoreholeSet Data(){if(Path.GetExtension(PathBox.Text).Equals(".pdf",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("PDF 추출표에서 ‘검토 완료 · 적용’을 누르세요.");var data=Read(PathBox.Text);data.Validate(standalone?data.Crs:settings.Frame.Crs);return data;}
    public void Use(string file){PathBox.Text=file;settings.BoreholeJson=file;UpdateSummary(Data());owner.SetStatus("시추자료 검토 · 연결 완료.");Loaded?.Invoke();}
    private void UpdateSummary(BoreholeSet data)=>summary.Text=$"{data.Boreholes.Count}공 / 지표 EL {data.Boreholes.Min(b=>b.GroundElevation):F2}~{data.Boreholes.Max(b=>b.GroundElevation):F2}m / 좌표계 {data.Crs}";
}
