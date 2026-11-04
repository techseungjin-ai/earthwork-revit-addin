using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CivilBim.Revit;

// Revit API access from a modeless window must run inside ExternalEvent.Execute.
public class ModelessWindow:Window
{
    internal ModelessSession? Session {get;set;}
    internal bool WasClosed {get;private set;}
    private readonly TextBlock status=new(){TextWrapping=TextWrapping.Wrap,Margin=new(14,8,14,12),Foreground=Brushes.DarkSlateBlue};
    public event Action? Submitted;
    public event Action<Exception>? Failed;
    internal void ReportFailure(Exception ex){SetStatus(ex.Message,true);Failed?.Invoke(ex);}
    public string StatusText=>status.Text;
    public void Accept()=>Submitted?.Invoke();
    public void SetStatus(string text,bool error=false){status.Text=text;status.Foreground=error?Brushes.Firebrick:Brushes.DarkSlateBlue;}
    public void Api(Action action,bool picking=false)
    {
        if(Session==null){action();return;} // Constructor is called in an external command/API context.
        Session.Request(_=>action(),picking);
    }
    public void Api(Action<UIApplication> action,bool picking=false)
    {
        if(Session==null)throw new InvalidOperationException("모델리스 창이 연결되지 않았습니다.");Session.Request(action,picking);
    }
    public void StickyActions(params UIElement[] actions)
    {
        var body=Content;Content=null;var dock=new DockPanel {Background=Brushes.White};var footer=new StackPanel {Margin=new(22,8,22,8)};DockPanel.SetDock(footer,Dock.Bottom);
        foreach(var action in actions){if(LogicalTreeHelper.GetParent(action) is System.Windows.Controls.Panel parent)parent.Children.Remove(action);footer.Children.Add(action);}
        dock.Children.Add(footer);dock.Children.Add(new ContentControl {Content=body});Content=dock;
    }
    internal void Mount()
    {
        var body=Content;Content=null;var root=new DockPanel {Background=Brushes.White};DockPanel.SetDock(status,Dock.Bottom);root.Children.Add(status);root.Children.Add(new ContentControl {Content=body});Content=root;
        Closed+=(_,_)=>{WasClosed=true;Session?.Closed();};
        SetStatus("창을 열어 둔 상태에서 Revit 뷰 전환·선택·이동이 가능합니다.");
    }
    public T Pick<T>(Func<T> action)
    {
        var visible=IsVisible;if(visible)Hide();try{return action();}finally{if(visible&&!WasClosed)Show();}
    }
    internal void Busy(bool value){if(Content is UIElement c)c.IsEnabled=!value;}
}

public sealed class ModelessSession:IExternalEventHandler
{
    private readonly Document document;
    private readonly ModelessWindow window;
    private readonly ExternalEvent external;
    private readonly Queue<(Action<UIApplication> Run,bool Pick)> requests=[];
    private bool pending,executing,disposed;
    private UIApplication? current;
    internal event Action<string>? Trace;
    internal ModelessSession(Document doc,ModelessWindow owner){document=doc;window=owner;external=ExternalEvent.Create(this);}
    public void Request(Action<UIApplication> action,bool picking=false)
    {
        if(disposed||window.WasClosed)return;
        if(executing&&current!=null){if(picking)window.Pick(()=>{action(current);return true;});else action(current);return;}
        requests.Enqueue((action,picking));if(executing||pending)return;
        pending=true;window.Busy(true);window.SetStatus("Revit 작업 대기 중… 진행 중인 명령은 완료하거나 Esc로 종료하세요.");
        var result=external.Raise();
        Trace?.Invoke("Raise: "+result);
        if(result is ExternalEventRequest.Denied or ExternalEventRequest.TimedOut){pending=false;requests.Clear();window.Busy(false);window.SetStatus("Revit이 요청을 받지 못했습니다. 현재 명령을 끝낸 뒤 다시 실행하세요.",true);}
    }
    public void Execute(UIApplication app)
    {
        Trace?.Invoke("Execute / same document: "+document.Equals(app.ActiveUIDocument?.Document)+" / active: "+app.ActiveUIDocument?.Document.PathName);
        pending=false;executing=true;current=app;
        try
        {
            if(window.WasClosed){requests.Clear();return;}
            if(!document.IsValidObject||!document.Equals(app.ActiveUIDocument?.Document))
            {requests.Clear();window.SetStatus("이 창을 연 프로젝트로 돌아오세요. 다른 프로젝트에는 작업을 적용하지 않았습니다.",true);return;}
            while(requests.Count>0&&!window.WasClosed)
            {
                var request=requests.Dequeue();
                try
                {
                    window.SetStatus("처리 중…");
                    if(request.Pick)window.Pick(()=>{request.Run(app);return true;});else request.Run(app);
                    if(window.StatusText=="처리 중…")window.SetStatus("완료. 창을 유지한 채 다음 작업을 진행할 수 있습니다.");
                }
                catch(Exception ex) when(ex is Autodesk.Revit.Exceptions.OperationCanceledException or System.OperationCanceledException){window.SetStatus("화면 선택을 취소했습니다. 입력값은 유지됩니다.");requests.Clear();}
                catch(Exception ex){window.ReportFailure(ex);requests.Clear();}
            }
        }
        finally{executing=false;current=null;if(!window.WasClosed)window.Busy(false);else Dispose();}
    }
    internal void Closed(){requests.Clear();if(!pending&&!executing)Dispose();}
    private void Dispose(){if(disposed)return;disposed=true;external.Dispose();}
    public string GetName()=>"CivilBIM 모델리스 작업";
}

public static class ModelessHost
{
    private static readonly Dictionary<(Document Doc,string Key),ModelessWindow> windows=[];
    public static void CloseOthers(Document doc,ModelessWindow keep)
    {foreach(var pair in windows.ToList())if(pair.Key.Doc.Equals(doc)&&pair.Value!=keep)pair.Value.Close();}
    public static T Open<T>(UIApplication app,string key,Func<T> create,Action<T>? setup=null) where T:ModelessWindow
    {
        var doc=app.ActiveUIDocument?.Document??throw new InvalidDataException("프로젝트 문서를 여세요.");
        var identity=(doc,key);
        if(windows.TryGetValue(identity,out var old)&&!old.WasClosed){old.Show();old.Activate();return (T)old;}
        var window=create();window.Session=new(doc,window);window.Mount();new System.Windows.Interop.WindowInteropHelper(window).Owner=app.MainWindowHandle;
        windows[identity]=window;window.Closed+=(_,_)=>windows.Remove(identity);setup?.Invoke(window);window.Show();return window;
    }
    public static void Review(UIApplication app,BoreholeSetInput input,Action<string> saved)
    {
        Open(app,"시추검토:"+Guid.NewGuid(),()=>new ReviewWindow(input.Data),w=>w.Submitted+=()=>{if(w.SavedPath is string path){saved(path);w.Close();}});
    }
}
public sealed record BoreholeSetInput(CivilBim.Core.BoreholeSet Data);
