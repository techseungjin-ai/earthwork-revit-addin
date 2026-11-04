using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace CivilBim.Core;

public static class BoreholePdf
{
    // report.pdf와 같은 포털 내보내기 양식. 다른 양식/스캔은 자동 추정하지 않고 검토로 넘긴다.
    private sealed record Token(string Text,double X,double Y);
    private static List<Token> Tokens(Page page)
    {
        return page.GetWords().Select(w=>new Token(w.Text,w.BoundingBox.Left,w.BoundingBox.Bottom)).ToList();
    }
    public static BoreholeSet ExtractCandidates(string path,string crs)
    {
        using var pdf=PdfDocument.Open(path);
        var output=new BoreholeSet {Crs=crs,Source=Path.GetFileName(path),Reviewed=false};
        output.ReviewNotes.Add("PDF 추출 후보입니다. 원문과 공번/좌표/지표 EL/지층 이름/하단 심도를 대조한 뒤 검토 완료를 선택하세요.");
        var maps=new Dictionary<string,List<(double Bottom,double Elevation,double? Thickness,string Name,int Page)>>();
        foreach(var page in pdf.GetPages())
        {
            var tokens=Tokens(page); var w=page.Width; var h=page.Height;
            var idToken=tokens.FirstOrDefault(t=>t.X/w>.80 && (h-t.Y)/h<.10 && Regex.IsMatch(t.Text,@"BH\s*[-–]?\s*\d+",RegexOptions.IgnoreCase));
            var id=idToken==null?"":Regex.Match(idToken.Text,@"BH\s*[-–]?\s*\d+",RegexOptions.IgnoreCase).Value.Replace(" ","");
            double Read(double x1,double x2,double dy)
            {
                var s=string.Join("",tokens.Where(t=>t.X/w>=x1 && t.X/w<x2 && Math.Abs(t.Y-(idToken!.Y-dy))<4).OrderBy(t=>t.X).Select(t=>t.Text));
                var m=Regex.Match(s,@"-?\d+(?:\.\d+)?");
                if(!m.Success) throw new InvalidDataException($"{page.Number}쪽 수치 영역 인식 실패 ({s}).");
                return Guard.Number(m.Value);
            }
            if(string.IsNullOrWhiteSpace(id)) {output.ReviewNotes.Add($"{page.Number}쪽: 지원 양식의 공번을 찾지 못했습니다. 스캔/다른 양식은 수동 입력 필요.");continue;}
            double east,north,ground;
            try {east=Read(.61,.75,26); north=Read(.61,.75,42); ground=Read(.82,.98,34);}
            catch(InvalidDataException ex) {output.ReviewNotes.Add(ex.Message); continue;}
            var existing=output.Boreholes.FirstOrDefault(b=>b.Id==id);
            if(existing==null) {existing=new(id,east,north,ground,[]);output.Boreholes.Add(existing);maps[id]=[];}
            else if(Math.Abs(existing.Easting-east)>.001 || Math.Abs(existing.Northing-north)>.001 || Math.Abs(existing.GroundElevation-ground)>.001)
                throw new InvalidDataException($"{id}: 페이지 간 좌표/지표 EL 불일치.");
            var depthTokens=tokens.Where(t=>t.X/w>=.035 && t.X/w<.085 && t.Y<idToken!.Y-180 && (h-t.Y)/h<.90 && Regex.IsMatch(t.Text,@"^\d+(?:\.\d+)?$")).OrderByDescending(t=>t.Y).ToList();
            var topY=idToken!.Y-180;
            foreach(var depthToken in depthTokens)
            {
                var bottom=Guard.Number(depthToken.Text);
                var elevationToken=tokens.FirstOrDefault(t=>t.X/w>.09 && t.X/w<.15 && Math.Abs(t.Y-depthToken.Y)<3 && Regex.IsMatch(t.Text,@"^-?\d+(?:\.\d+)?$"));
                var thickToken=tokens.FirstOrDefault(t=>t.X/w>.16 && t.X/w<.21 && Math.Abs(t.Y-depthToken.Y)<3 && Regex.IsMatch(t.Text,@"^-?\d+(?:\.\d+)?$"));
                var nameTokens=tokens.Where(t=>t.X/w>.27 && t.X/w<.34 && t.Y<=topY+4 && t.Y>depthToken.Y-1).OrderByDescending(t=>t.Y).ToList();
                var name=string.Join("",nameTokens.Where(t=>Regex.IsMatch(t.Text,"[가-힣]")).Take(3).Select(t=>t.Text));
                // 색상/설명은 이름에 포함하지 않는다. 해독 불가 한글은 원문 검토 대상으로 남긴다.
                var match=Regex.Match(name,"매립토층|퇴적토층|풍화토층|풍화암층|연암층|보통암층|경암층");
                name=match.Success?match.Value:"검토필요";
                if(elevationToken==null) {output.ReviewNotes.Add($"{id} {page.Number}쪽 심도 {bottom}: 표고 누락.");continue;}
                var elevation=Guard.Number(elevationToken.Text);
                if(Math.Abs(ground-bottom-elevation)>.03) output.ReviewNotes.Add($"{id} {page.Number}쪽 심도/표고 불일치: {bottom}, EL {elevation}.");
                maps[id].Add((bottom,elevation,thickToken==null?null:Guard.Number(thickToken.Text),name,page.Number));
                topY=depthToken.Y;
            }
        }
        foreach(var borehole in output.Boreholes)
        {
            var rows=maps[borehole.Id].GroupBy(x=>x.Bottom).OrderBy(x=>x.Key).ToList(); var previous=0.0;
            foreach(var group in rows)
            {
                var row=group.First();
                if(group.Any(x=>Math.Abs(x.Elevation-row.Elevation)>.03)) output.ReviewNotes.Add($"{borehole.Id}: 반복 페이지의 경계 값 불일치.");
                if(row.Thickness.HasValue && Math.Abs(row.Thickness.Value-(row.Bottom-previous))>.03)
                    output.ReviewNotes.Add($"{borehole.Id} {row.Page}쪽: 표기 층후 {row.Thickness}와 심도차 {row.Bottom-previous:F2} 불일치. 하단 심도 후보는 유지.");
                borehole.Layers.Add(new(row.Name,row.Bottom));
                if(row.Name=="검토필요") output.ReviewNotes.Add($"{borehole.Id} 심도 {row.Bottom}: 지층명을 원문에서 입력하세요.");
                previous=row.Bottom;
            }
        }
        if(output.Boreholes.Count==0) throw new InvalidDataException("지원 양식의 텍스트를 찾지 못했습니다. 스캔 PDF OCR은 아직 지원하지 않습니다. JSON 입력을 사용하세요.");
        return output;
    }
}
