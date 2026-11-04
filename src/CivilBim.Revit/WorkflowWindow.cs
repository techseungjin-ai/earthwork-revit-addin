using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using CivilBim.Core;
using Autodesk.Revit.DB;

namespace CivilBim.Revit;

public sealed class WorkflowWindow : ModelessWindow
{
    private readonly Dictionary<string,TextBox> fields=[];
    private readonly Dictionary<string,ComboBox> layers=[];
    private readonly ComboBox scale=new() {ItemsSource=new[]{1000,5000},MinWidth=150};
    private readonly CheckBox centerline=new() {Content="CIP 중심선에 간격 배치 (해제 시 원 중심 인식)",Margin=new(0,6,0,6)};
    private readonly CheckBox coordinatesChecked=new() {Content="SHP PRJ와 시추 좌표계 및 CAD 위치·단위 정합을 확인했습니다.",Margin=new(0,10,0,10)};
    private readonly Dictionary<string,ComboBox> families=[];
    private readonly Document? document;
    private readonly ComboBox cadPicker=new(){MinHeight=32};
    public ImportInstance? CurrentCad=>(cadPicker.SelectedItem as CadChoice)?.Instance;
    private readonly CheckBox originAtTop=new() {Content="단일 레벨 수직 패밀리의 원점은 상단입니다 (해제: 하단)",Margin=new(0,8,0,8)};
    private readonly CheckBox softRock=new() {Content="CIP 하단을 최근접 시추공의 연암층 상단으로 결정 (검토된 시추 JSON 필요)",Margin=new(0,8,0,8)};
    public Settings Settings {get;private set;}
    public string Action {get;private set;}="";
    private readonly TabControl tabs=new();
    public int SelectedTab=>tabs.SelectedIndex;
    public WorkflowWindow(Settings settings,string[] cadLayers,FamilyChoice[] familyChoices,int selectedTab=0,Document? doc=null)
    {
        document=doc;Settings=settings;Title="CivilBIM · Revit 2027";Width=880;Height=790;MinWidth=700;MinHeight=600;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel {Margin=new(20)};Content=root;
        var heading=new TextBlock {Text="토공 · 흙막이 모델링",FontSize=24,FontWeight=FontWeights.SemiBold,Margin=new(0,0,0,12)};
        DockPanel.SetDock(heading,Dock.Top);root.Children.Add(heading);
        root.Children.Add(tabs);
        StackPanel Tab(string title)
        {
            var panel=new StackPanel {Margin=new(15)};tabs.Items.Add(new TabItem {Header=title,Content=new ScrollViewer {Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});return panel;
        }
        var site=Tab("1 좌표 · SHP");
        Note(site,"모든 길이·표고는 m, 방위 회전은 도입니다. 원본 축척은 좌표 배율이 아닙니다. 좌표계 ID는 PRJ를 확인해 입력하세요.");
        Field(site,"crs","좌표계 ID (예: EPSG:5186)",settings.Frame.Crs);
        Field(site,"east","원점 Easting",settings.Frame.Easting); Field(site,"north","원점 Northing",settings.Frame.Northing);
        Field(site,"elevation","원점 표고 EL",settings.Frame.Elevation);Field(site,"rotation","GIS → Revit 반시계 회전",settings.Frame.RotationDegrees);
        site.Children.Add(coordinatesChecked);
        coordinatesChecked.IsChecked=settings.CoordinatesConfirmed;
        FileField(site,"shp","등고선/표고점 SHP (복수 선택 가능)",settings.Shp,"SHP|*.shp",true);
        Field(site,"zfield","DBF 표고 필드 (Z형은 비워도 됨)",settings.ElevationField);
        site.Children.Add(new TextBlock {Text="원본 축척"});scale.SelectedItem=settings.Scale;site.Children.Add(scale);
        Field(site,"sampling","등고선 샘플 간격",settings.Sampling);
        Field(site,"terraindepth","지형 두께 (굴착 깊이보다 충분히 크게)",settings.TerrainDepth);
        Note(site,"점 기반 Toposolid의 경계는 점들의 외곽으로 생성됩니다. 오목한 대지 경계·건물·도로 자동 작성은 현재 범위에 포함되지 않습니다.");
        Button(site,"SHP 속성 · 범위 확인","InspectShp");Button(site,"지형 생성","Terrain");Button(site,"좌표 설정 저장","Save");

        var cad=Tab("2 굴착 · 가시설");
        if(doc!=null)
        {
            cad.Children.Add(new TextBlock {Text="CAD 링크 / 가져오기"});var imports=ScreenChoices.Imports(doc);cadPicker.ItemsSource=imports;ScreenChoices.Cad(this,cad,cadPicker,doc);
            cadPicker.SelectionChanged+=(_,_)=>Api(()=>{if(CurrentCad is ImportInstance selected){Settings.CadUniqueId=selected.UniqueId;UpdateLayers(CadReader.Read(doc,selected).Layers);}});
            cadPicker.SelectedItem=imports.FirstOrDefault(c=>c.Instance.UniqueId==settings.CadUniqueId);
        }
        Note(cad,"Revit에 DWG/DXF를 링크하고 선택한 뒤 ‘CAD 선택’을 누르세요. 부재별 전용 레이어를 지정합니다. 자동 구조 설계가 아니라 명시한 중심·중심선의 형상을 작성합니다.");
        Button(cad,"CAD 선택 · 레이어 분석","PickCad");
        Button(cad,"패밀리 RFA 로드","LoadFamily");
        Family(cad,"cipfamily","CIP 패밀리 유형",settings.CipFamily,familyChoices);
        Field(cad,"ciplength","CIP 인스턴스 길이 파라미터 (구조 기둥은 레벨 제약 사용)",settings.CipLengthParameter);
        Field(cad,"cipdiameter","CIP 직경 파라미터 (공란: 유형 치수 사용)",settings.CipDiameterParameter);
        cad.Children.Add(originAtTop);originAtTop.IsChecked=settings.PileOriginAtTop;
        Layer(cad,"cip","CIP 원/중심선 레이어",settings.CipLayer,cadLayers);
        cad.Children.Add(centerline);centerline.IsChecked=settings.CenterlineCip;
        Field(cad,"diameter","CIP 직경",settings.Diameter);Field(cad,"spacing","CIP 중심선 배치 간격",settings.PileSpacing);
        Field(cad,"top","CIP / H-Pile / 토류벽 상단 EL",settings.PileTop);Field(cad,"bottom","CIP / H-Pile / 토류벽 하단 EL",settings.PileBottom);
        Button(cad,"DWG 단면 · 전개도 EL/길이 후보 인식","Depths");
        cad.Children.Add(softRock);softRock.IsChecked=settings.CipBottomAtSoftRock;
        Button(cad,"현재 CAD의 평면 인식 영역 지정","CropCad");
        Layer(cad,"hpile","H-Pile 배치 원 중심 레이어",settings.HPileLayer,cadLayers);
        Family(cad,"hpilefamily","H-Pile 패밀리 유형",settings.HPileFamily,familyChoices);
        Field(cad,"hpilelength","H-Pile 인스턴스 길이 파라미터",settings.HPileLengthParameter);
        Layer(cad,"wall","토류벽 중심선 레이어",settings.WallLayer,cadLayers);
        Field(cad,"wallthickness","토류벽 두께",settings.WallThickness);
        Family(cad,"wallfamily","토류벽 선 기반 패밀리 유형",settings.WallFamily,familyChoices);
        Field(cad,"wallheight","토류벽 높이 파라미터",settings.WallHeightParameter);
        Layer(cad,"waler","띠장 중심선 레이어",settings.WalerLayer,cadLayers);Layer(cad,"strut","버팀대 중심선 레이어",settings.StrutLayer,cadLayers);
        Field(cad,"levels","띠장/버팀대 중심 EL 목록 (쉼표 구분)",settings.WalerElevations);
        Family(cad,"walerfamily","띠장 패밀리 유형",settings.WalerFamily,familyChoices);
        Family(cad,"strutfamily","STRUT 패밀리 유형",settings.StrutFamily,familyChoices);
        Note(cad,"선택한 패밀리로 작성합니다. 수직 부재는 비호스트 단일 레벨/구조 기둥, 수평 부재는 선 기반/구조 프레임을 지원합니다. 유형 치수는 패밀리 유형에서 선택합니다.");
        Button(cad,"가시설 생성 전 개수 확인","PreviewCad");Button(cad,"CIP · H-Pile · 띠장 · 버팀대 · 토류벽 생성","Shoring");
        Layer(cad,"excavation","굴착 경계 (폐합 폴리선 1개) 레이어",settings.ExcavationLayer,cadLayers);
        Field(cad,"excavationel","굴착 바닥 EL",settings.ExcavationElevation);
        Note(cad,"굴착은 CivilBIM 지형 1개에 수직 경계로 적용합니다. 다단 굴착·사면·암종별 토공량은 추가 개발 대상입니다.");
        Button(cad,"굴착 적용","Excavate");

        var geology=Tab("3 시추 · CIP 분할");
        Note(geology,"시추주상도 PDF를 로컬에서 읽고 JSON 후보를 표시합니다. 원문과 대조·수정 후 검토 완료로 저장하세요. 포털 내보내기 양식을 지원하며 스캔 OCR은 아직 지원하지 않습니다.");
        FileField(geology,"borehole","시추자료 JSON",settings.BoreholeJson,"JSON|*.json");
        Button(geology,"PDF 선택 → 추출 후보 검토","Pdf");Button(geology,"JSON 열기 → 검토 / 수정","Review");
        Field(geology,"radius","최근접 시추공 최대 영향반경",settings.Radius);
        Note(geology,"선택한 방법: 최근접 시추공. 각 말뚝 위치에 가장 가까운 공의 표고 경계로 분할합니다. 조사범위 밖 말뚝은 오류로 알립니다.");
        Button(geology,"기존 CivilBIM CIP 지층 분할 / 재분할","Split");
        var quantity=Tab("4 물량");
        Note(quantity,"CSV에는 말뚝 ID, 지층, 상하단 EL, 직경, 길이, 체적, 적용 시추공, 원본, 모델 Element ID를 기록합니다. 수량은 생성 당시 계산값이며 변경된 모델과 대조합니다.");
        Button(quantity,"물량 CSV 내보내기","Export");
        tabs.SelectedIndex=selectedTab;
        foreach(var key in new[]{"crs","east","north","elevation","rotation"})fields[key].TextChanged+=(_,_)=>coordinatesChecked.IsChecked=false;
    }
    private static void Note(StackPanel p,string text)=>p.Children.Add(new TextBlock {Text=text,TextWrapping=TextWrapping.Wrap,Margin=new(0,4,0,12)});
    private TextBox Field(StackPanel p,string key,string label,object value)
    {
        p.Children.Add(new TextBlock {Text=label,Margin=new(0,7,0,3)});
        var box=new TextBox {Text=Convert.ToString(value,CultureInfo.InvariantCulture)??"",Padding=new(6),MinHeight=30};p.Children.Add(box);fields[key]=box;return box;
    }
    private void FileField(StackPanel p,string key,string label,string value,string filter,bool multi=false)
    {
        var box=Field(p,key,label,value);var button=new Button {Content="파일 찾기",HorizontalAlignment=HorizontalAlignment.Right,Padding=new(10,4,10,4)};
        button.Click+=(_,_)=>{var d=new OpenFileDialog {Filter=filter,Multiselect=multi};if(d.ShowDialog(this)==true)box.Text=string.Join("|",d.FileNames);};p.Children.Add(button);
    }
    private void Layer(StackPanel p,string key,string label,string value,string[] items)
    {
        p.Children.Add(new TextBlock {Text=label,Margin=new(0,7,0,3)});var c=new ComboBox {ItemsSource=new[]{""}.Concat(items).Distinct().ToArray(),MinHeight=30,IsEditable=true,Text=value};if(document!=null)ScreenChoices.Layer(this,p,c,cadPicker,document);else p.Children.Add(c);layers[key]=c;
    }
    private void Family(StackPanel p,string key,string label,string value,FamilyChoice[] choices)
    {
        p.Children.Add(new TextBlock {Text=label,Margin=new(0,7,0,3)});
        var c=new ComboBox {ItemsSource=new[]{new FamilyChoice("","선택하세요")}.Concat(choices).ToArray(),MinHeight=30};
        c.SelectedIndex=0;c.SelectedItem=((FamilyChoice[])c.ItemsSource).FirstOrDefault(x=>x.Id==value)??c.Items[0];if(document!=null)ScreenChoices.Family(this,p,c,document,key is "cipfamily" or "hpilefamily"?ScreenChoices.Vertical:ScreenChoices.Horizontal);else p.Children.Add(c);families[key]=c;
    }
    private void Button(StackPanel p,string text,string action)
    {
        var b=new Button {Content=text,Padding=new(10,8,10,8),Margin=new(0,12,0,2),HorizontalAlignment=HorizontalAlignment.Left};
        b.Click+=(_,_)=>
        {
            try {ReadSettings();Action=action;Accept();} catch(Exception ex) {SetStatus(ex.Message,true);}
        };p.Children.Add(b);
    }
    private void ReadSettings()
    {
        string S(string key)=>fields[key].Text.Trim();double N(string key){var n=Guard.Number(S(key));Guard.Finite(n);return n;}
        string L(string key)=>layers[key].Text.Trim();
        string F(string key)=>(families[key].SelectedItem as FamilyChoice)?.Id??"";
        var prior=Settings;
        var frame=new SiteFrame(S("crs"),N("east"),N("north"),N("elevation"),N("rotation"));
        // 파일 검사/CAD 선택은 좌표 설정 전에도 가능하다. 모델 변경 시 명시적 확인한다.
        Settings=new Settings {Frame=frame,Shp=S("shp"),ElevationField=S("zfield"),Scale=(int)(scale.SelectedItem??1000),Sampling=N("sampling"),
            PileTop=N("top"),PileBottom=N("bottom"),Diameter=N("diameter"),PileSpacing=N("spacing"),CenterlineCip=centerline.IsChecked==true,
            CipLayer=L("cip"),HPileLayer=L("hpile"),WallLayer=L("wall"),WalerLayer=L("waler"),StrutLayer=L("strut"),ExcavationLayer=L("excavation"),
            WalerElevations=S("levels"),WallThickness=N("wallthickness"),
            ExcavationElevation=N("excavationel"),BoreholeJson=S("borehole"),Radius=N("radius"),Method="Nearest"};
        Settings.CipFamily=F("cipfamily");Settings.HPileFamily=F("hpilefamily");Settings.WalerFamily=F("walerfamily");Settings.StrutFamily=F("strutfamily");Settings.WallFamily=F("wallfamily");
        Settings.CipLengthParameter=S("ciplength");Settings.CipDiameterParameter=S("cipdiameter");Settings.HPileLengthParameter=S("hpilelength");Settings.WallHeightParameter=S("wallheight");Settings.PileOriginAtTop=originAtTop.IsChecked==true;Settings.TerrainDepth=N("terraindepth");
        Settings.CipBottomAtSoftRock=softRock.IsChecked==true;
        Settings.CoordinatesConfirmed=coordinatesChecked.IsChecked==true;
        Settings.CadUniqueId=prior.CadUniqueId;Settings.PlanMin=prior.PlanMin;Settings.PlanMax=prior.PlanMax;
        Settings.SiteFolder=prior.SiteFolder;Settings.TerrainType=prior.TerrainType;Settings.RoadType=prior.RoadType;Settings.BuildingMaterial=prior.BuildingMaterial;Settings.RoadOffset=prior.RoadOffset;Settings.BuildingFloorHeight=prior.BuildingFloorHeight;
        Settings.CipTopParameter=prior.CipTopParameter;Settings.HPileTopParameter=prior.HPileTopParameter;Settings.HPileRotationOffset=prior.HPileRotationOffset;
        Settings.HasExcavationElevation=prior.HasExcavationElevation;Settings.WorksiteBoundaryWkt=prior.WorksiteBoundaryWkt;
    }
    public void SetBorehole(string path){fields["borehole"].Text=path;Settings.BoreholeJson=path;}
    public void SetCad(ImportInstance instance){if(document==null)return;var values=ScreenChoices.Imports(document);cadPicker.ItemsSource=values;cadPicker.SelectedItem=values.FirstOrDefault(v=>v.Instance.Id==instance.Id);}
    public void SetDepth(DepthReviewWindow review){fields["top"].Text=review.TopElevation.ToString(CultureInfo.InvariantCulture);fields["bottom"].Text=review.Bottom.ToString(CultureInfo.InvariantCulture);softRock.IsChecked=review.SoftRock;if(review.Diameter.HasValue)fields["diameter"].Text=review.Diameter.Value.ToString(CultureInfo.InvariantCulture);}
    public void UpdateLayers(string[] values){foreach(var c in layers.Values){var text=c.Text;c.ItemsSource=new[]{""}.Concat(values).Distinct().ToArray();c.Text=text;}}
    public void UpdateFamilies(FamilyChoice[] values){foreach(var c in families.Values){var id=(c.SelectedItem as FamilyChoice)?.Id;c.ItemsSource=new[]{new FamilyChoice("","선택하세요")}.Concat(values).ToArray();c.SelectedItem=((FamilyChoice[])c.ItemsSource).FirstOrDefault(v=>v.Id==id)??c.Items[0];}}
    public bool CoordinateConfirmation=>coordinatesChecked.IsChecked==true;
}
