using Autodesk.Revit.UI;

namespace CivilBim.Revit;

public sealed class App : IExternalApplication
{
    public Result OnStartup(UIControlledApplication app)
    {
        const string tab="토공사";
        try {app.CreateRibbonTab(tab);} catch(Autodesk.Revit.Exceptions.ArgumentException) { }
        var site=app.CreateRibbonPanel(tab,"대지 작성 / 수정");
        site.AddItem(Button("CivilBimSite","SHP 지형·도로\n주변건물",typeof(App).Assembly.Location,typeof(ContextCommand).FullName!));
        site.AddItem(Button("CivilBimSiteAlign","대지 방향\n선으로 맞춤",typeof(App).Assembly.Location,typeof(SiteAlignmentCommand).FullName!));
        site.AddItem(Button("CivilBimRoadMerge","도로 구획\n합치기",typeof(App).Assembly.Location,typeof(RoadMergeCommand).FullName!));
        site.AddItem(Button("CivilBimTerrainMove","지형점\n일괄 이동",typeof(App).Assembly.Location,typeof(TerrainMoveCommand).FullName!));
        site.AddItem(Button("CivilBimTerrainZ","측량 문자\n표고 맞춤",typeof(App).Assembly.Location,typeof(TerrainElevationCommand).FullName!));
        site.AddItem(Button("CivilBimExcavationBoundary","CAD 경계\n굴착면 작성",typeof(App).Assembly.Location,typeof(ExcavationBoundaryCommand).FullName!));
        var panel=app.CreateRibbonPanel(tab,"가시설 / 지층");
        panel.AddItem(Button("CivilBimCip","CIP\n일괄 배치",typeof(App).Assembly.Location,typeof(CipPlacementCommand).FullName!));
        panel.AddItem(Button("CivilBimHPile","H-PILE\n인식·회전",typeof(App).Assembly.Location,typeof(HPilePlacementCommand).FullName!));
        panel.AddItem(Button("CivilBimDepth2D","경로 단면·시트\n높이 맞춤",typeof(App).Assembly.Location,typeof(DrawingDevelopmentCommand).FullName!));
        panel.AddItem(Button("CivilBimStrut","STRUT·띠장\n전개도 다단",typeof(App).Assembly.Location,typeof(HorizontalCommand).FullName!));
        var advanced=app.CreateRibbonPanel(tab,"추가 도구");
        advanced.AddItem(Button("CivilBimSoil","작업대지\n지층 생성",typeof(App).Assembly.Location,typeof(SoilCommand).FullName!));
        advanced.AddItem(Button("CivilBimSoilOverlap","지형·지층\n중복체적",typeof(App).Assembly.Location,typeof(SoilOverlapCommand).FullName!));
        advanced.AddItem(Button("CivilBimCipSplit","CIP\n지층 분할",typeof(App).Assembly.Location,typeof(CipSplitCommand).FullName!));
        var wizard=Button("CivilBimWizard","기존 통합창\nCSV / 추가 도구",typeof(App).Assembly.Location,typeof(WorkflowCommand).FullName!);
        wizard.ToolTip="이전 개발본 통합 창입니다. 대지·수직·수평 부재는 전용 명령을 사용하세요.";advanced.AddItem(wizard);
        SmokeRunner.Schedule(app);
        return Result.Succeeded;
    }
    private static PushButtonData Button(string name,string label,string assembly,string command)
    {
        var colors=new Dictionary<string,string>{{"CivilBimSite","#179B53"},{"CivilBimSiteAlign","#7A3FB1"},{"CivilBimRoadMerge","#1676D2"},{"CivilBimTerrainMove","#7A3FB1"},{"CivilBimTerrainZ","#E8640C"},{"CivilBimCip","#C92D36"},{"CivilBimHPile","#125AB1"},{"CivilBimDepth2D","#2673B7"},{"CivilBimStrut","#344C58"},{"CivilBimSoil","#A96B36"},{"CivilBimSoilOverlap","#617E53"},{"CivilBimCipSplit","#B94836"},{"CivilBimWizard","#586779"}};
        var glyphs=new Dictionary<string,string>{{"CivilBimSite","山"},{"CivilBimSiteAlign","↻"},{"CivilBimRoadMerge","↔"},{"CivilBimTerrainMove","✥"},{"CivilBimTerrainZ","↑"},{"CivilBimExcavationBoundary","▱"},{"CivilBimCip","●●"},{"CivilBimHPile","H"},{"CivilBimDepth2D","↕"},{"CivilBimStrut","═"},{"CivilBimSoil","≋"},{"CivilBimSoilOverlap","◉"},{"CivilBimCipSplit","≡"},{"CivilBimWizard","+"}};
        var visual=new System.Windows.Media.DrawingVisual();using(var dc=visual.RenderOpen())
        {
            var brush=(System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(colors.GetValueOrDefault(name,"#586779"))!;
            dc.DrawEllipse(brush,null,new System.Windows.Point(16,16),15,15);
            var text=new System.Windows.Media.FormattedText(glyphs.GetValueOrDefault(name,"+"),System.Globalization.CultureInfo.InvariantCulture,System.Windows.FlowDirection.LeftToRight,new System.Windows.Media.Typeface("Segoe UI"),name=="CivilBimCip"?11:20,System.Windows.Media.Brushes.White,1);
            dc.DrawText(text,new System.Windows.Point((32-text.Width)/2,(32-text.Height)/2));
        }
        var image=new System.Windows.Media.Imaging.RenderTargetBitmap(32,32,96,96,System.Windows.Media.PixelFormats.Pbgra32);image.Render(visual);image.Freeze();
        return new PushButtonData(name,label,assembly,command){LargeImage=image,Image=image};
    }
    public Result OnShutdown(UIControlledApplication app)=>Result.Succeeded;
}
