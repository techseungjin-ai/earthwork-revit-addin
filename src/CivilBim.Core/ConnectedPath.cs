namespace CivilBim.Core;

public static class ConnectedPath
{
    public static List<Point3> Cad(IReadOnlyList<Point3> points,double tolerance=.001)
    {
        var pieces=new List<IReadOnlyList<Point3>>();
        for(var i=1;i<points.Count;i++)
        {
            var a=points[i-1];var b=points[i];if(a.DistanceXY(b)<=tolerance)continue;
            bool Near(Point3 p,Point3 q)=>p.DistanceXY(q)<=tolerance&&Math.Abs(p.Z-q.Z)<=tolerance;
            if(pieces.Any(p=>Near(p[0],a)&&Near(p[1],b)||Near(p[0],b)&&Near(p[1],a)))continue;
            pieces.Add(new[]{a,b});
        }
        return Join(pieces,tolerance);
    }
    public static List<Point3> Join(IEnumerable<IReadOnlyList<Point3>> pieces,double tolerance=.001)
    {
        var input=pieces.Select(p=>p.ToList()).ToList();if(input.Count==0)throw new InvalidDataException("Revit 경로선을 선택하세요.");
        var nodes=new List<Point3>();var edges=new List<(int A,int B,List<Point3> Points)>();
        int Node(Point3 p){var matches=nodes.Select((v,i)=>(v,i)).Where(x=>x.v.DistanceXY(p)<=tolerance).ToList();if(matches.Count>1)throw new InvalidDataException("경로 끝점이 너무 가깝게 겹칩니다.");if(matches.Count==1)return matches[0].i;nodes.Add(p);return nodes.Count-1;}
        foreach(var p in input)
        {
            if(p.Count<2||p.Zip(p.Skip(1),(a,b)=>a.DistanceXY(b)).Sum()<=tolerance)throw new InvalidDataException("경로에 너무 짧은 선이 있습니다.");
            if(p.Any(q=>Math.Abs(q.Z-p[0].Z)>tolerance))throw new InvalidDataException("평면 경로선의 높이가 서로 다릅니다.");
            if(Math.Abs(p[0].Z-input[0][0].Z)>tolerance)throw new InvalidDataException("모든 경로선은 같은 높이에 있어야 합니다.");
            if(edges.Any(e=>e.Points.Count==p.Count&&(e.Points.Zip(p).All(v=>v.First.DistanceXY(v.Second)<=tolerance)||e.Points.Zip(p.AsEnumerable().Reverse()).All(v=>v.First.DistanceXY(v.Second)<=tolerance))))throw new InvalidDataException("같은 경로선이 겹쳐 있습니다. 중복 선을 제외하세요.");
            edges.Add((Node(p[0]),Node(p[^1]),p));
        }
        var adjacent=nodes.Select(_=>new List<int>()).ToArray();for(var i=0;i<edges.Count;i++){adjacent[edges[i].A].Add(i);adjacent[edges[i].B].Add(i);}
        if(adjacent.Any(a=>a.Count>2))throw new InvalidDataException("경로가 갈라지거나 같은 선이 중복 선택됐습니다. 이어진 한 줄만 선택하세요.");
        var ends=Enumerable.Range(0,nodes.Count).Where(i=>adjacent[i].Count==1).ToList();
        if(ends.Count!=0&&ends.Count!=2)throw new InvalidDataException("경로가 끊겨 있습니다. 모든 선 끝점을 연결하세요.");
        var start=ends.Count==0?edges[0].A:ends.OrderBy(i=>nodes[i].DistanceXY(input[0][0])).First();var current=start;var used=new HashSet<int>();var result=new List<Point3>();
        while(true)
        {
            var choices=adjacent[current].Where(i=>!used.Contains(i)).ToList();if(choices.Count==0)break;
            var index=choices[0];used.Add(index);var edge=edges[index];var forward=edge.A==current;var p=forward?edge.Points:edge.Points.AsEnumerable().Reverse().ToList();
            if(result.Count==0)result.Add(nodes[current]);result.AddRange(p.Skip(1));current=forward?edge.B:edge.A;result[^1]=nodes[current];
        }
        if(used.Count!=edges.Count)throw new InvalidDataException("서로 떨어진 경로가 함께 선택됐습니다. 연결된 선만 선택하세요.");return result;
    }
    public static (int Segment,double Distance) Nearest(Point3 p,IReadOnlyList<Point3> path)
    {
        if(path.Count<2)throw new InvalidDataException("경로가 없습니다.");var best=(Segment:-1,Distance:double.PositiveInfinity);
        for(var i=0;i<path.Count-1;i++){var a=path[i];var b=path[i+1];var dx=b.X-a.X;var dy=b.Y-a.Y;var length=dx*dx+dy*dy;if(length<1e-12)continue;var t=Math.Clamp(((p.X-a.X)*dx+(p.Y-a.Y)*dy)/length,0,1);var d=p.DistanceXY(new(a.X+t*dx,a.Y+t*dy));if(d<best.Distance)best=(i,d);}
        if(best.Segment<0)throw new InvalidDataException("길이가 있는 경로가 없습니다.");return best;
    }
}
