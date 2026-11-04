using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace CivilBim.Revit;

[Transaction(TransactionMode.Manual)]
public sealed class ContextCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)
    {
        var ui=data.Application.ActiveUIDocument;if(ui==null || ui.Document.IsFamilyDocument)return Result.Cancelled;
        ModelessHost.Open(data.Application,"대지",()=>new ContextWindow(ui.Document,Storage.Load(ui.Document)),window=>window.Submitted+=()=>window.Api(app=>
        {
            var result=ContextBuilder.Generate(ui.Document,window.Settings);app.ActiveUIDocument.RequestViewChange(result.View);
            var log=Path.Combine(Path.GetTempPath(),"CivilBIM-site-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".txt");File.WriteAllLines(log,result.Log);
            window.SetStatus(string.Join("\n",result.Log.Take(5))+"\n"+result.Log[^1]+"\n전체 로그: "+log);
        }));return Result.Succeeded;
    }
}

