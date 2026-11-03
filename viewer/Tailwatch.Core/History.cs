namespace Tailwatch.Core;

public readonly record struct HistoryPoint(TimeSpan Age,double? Cpu,double? Gpu,double? Ram);

/// <summary>Session-only history; callers serialize access with the monitor lock.</summary>
public sealed class MetricHistory(TimeProvider? timeProvider=null)
{
    public static readonly TimeSpan Window=TimeSpan.FromMinutes(15);
    readonly TimeProvider clock=timeProvider??TimeProvider.System;
    readonly Queue<(long At,double? Cpu,double? Gpu,double? Ram)> points=new();
    string? boot;
    ulong sequence;
    public void Record(string bootId,ulong number,double? cpu,double? gpu,double? ram)
    {
        Prune();
        if(boot==bootId&&number<=sequence)return;
        if(boot!=null&&boot!=bootId)Gap();
        boot=bootId;sequence=number;
        Append(cpu,gpu,ram);
    }
    public void Gap()
    {
        Prune();
        if(points.Count==0)return;
        var last=points.Last();
        if(last.Cpu!=null||last.Gpu!=null||last.Ram!=null)Append(null,null,null);
    }
    void Append(double? cpu,double? gpu,double? ram)
    {
        points.Enqueue((clock.GetTimestamp(),cpu,gpu,ram));
        while(points.Count>451)points.Dequeue();
    }
    void Prune()
    {
        while(points.TryPeek(out var point)&&clock.GetElapsedTime(point.At)>Window)points.Dequeue();
    }
    public IReadOnlyList<HistoryPoint> Read()
    {
        Prune();var now=clock.GetTimestamp();
        return points.Select(p=>new HistoryPoint(clock.GetElapsedTime(p.At,now),p.Cpu,p.Gpu,p.Ram)).ToArray();
    }
    public void Clear(){points.Clear();boot=null;sequence=0;}
}
