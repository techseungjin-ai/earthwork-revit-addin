using System.Diagnostics;
using System.Reflection;

namespace CivilBim.Revit;

public static class DwgExtractor
{
    public static string Extract(string source)
    {
        var exe=@"C:\Program Files\Autodesk\AutoCAD 2027\accoreconsole.exe";
        if(!File.Exists(exe))throw new InvalidDataException("DWG 문자 추출에는 설치된 AutoCAD 2027 Core Console가 필요합니다. 모델 생성은 Revit에서 수행합니다.");
        var folder=Path.Combine(Path.GetTempPath(),"CivilBIM",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var copy=Path.Combine(folder,"input.dwg");File.Copy(source,copy);
        var script=Path.Combine(folder,"extract.scr");
        using(var input=Assembly.GetExecutingAssembly().GetManifestResourceStream("CivilBim.ExtractDwg.scr")??throw new InvalidOperationException("추출 스크립트 누락."))
        using(var output=File.Create(script))input.CopyTo(output);
        var start=new ProcessStartInfo(exe) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=folder};
        foreach(var arg in new[]{"/i",copy,"/s",script,"/l","en-US"})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new InvalidOperationException("DWG 추출 실행 실패.");
        var stdout=process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);var stderr=process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        if(!process.WaitForExit(120000)){process.Kill(true);throw new InvalidOperationException("DWG 추출 제한 시간 초과.");}
        Task.WhenAll(stdout,stderr).GetAwaiter().GetResult();
        var extracted=Path.Combine(folder,"drawing-extract.txt");
        if(!File.Exists(extracted) || new FileInfo(extracted).Length==0)throw new InvalidDataException("DWG 문자 추출 결과가 없습니다.");return extracted;
    }
}
