namespace CivilBim.Core;

// In a section/sheet coordinate frame, X is horizontal and Y is model height (m).
public static class SectionLineHeight
{
    public static double? At(double x,IEnumerable<PlanSegment> lines)
    {
        var heights=new List<double>();
        foreach(var line in lines)
        {
            var dx=line.B.X-line.A.X;if(Math.Abs(dx)<1e-10)continue;
            var t=(x-line.A.X)/dx;if(t< -1e-7||t>1+1e-7)continue;
            heights.Add(line.A.Y+Math.Clamp(t,0,1)*(line.B.Y-line.A.Y));
        }
        if(heights.Count==0)return null;
        if(heights.Max()-heights.Min()>.002)throw new InvalidDataException("부재 위치에서 기준선 높이가 여러 개입니다. 상단/하단 선을 따로 선택하세요.");
        return heights.Average();
    }
}
