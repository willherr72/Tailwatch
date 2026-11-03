using Tailwatch.Core;
using Xunit;
namespace Tailwatch.Tests;
public class HistoryTests
{
    sealed class Clock:TimeProvider
    {
        public long Ticks=1;
        public override long TimestampFrequency=>1000;
        public override long GetTimestamp()=>Ticks;
        public override DateTimeOffset GetUtcNow()=>DateTimeOffset.UnixEpoch.AddYears(10); // Wall clock is unrelated.
    }
    [Fact] public void RetentionUsesElapsedTimeAndHasAHardPointLimit()
    {
        var clock=new Clock();var history=new MetricHistory(clock);
        for(ulong i=0;i<1000;i++){history.Record("boot",i,20,30,40);clock.Ticks+=100;}
        Assert.InRange(history.Read().Count,1,451);
        clock.Ticks+=15*60*1000;Assert.Empty(history.Read());
    }
    [Fact] public void DuplicateSamplesDoNotInventNewMeasurements()
    {
        var clock=new Clock();var history=new MetricHistory(clock);
        history.Record("boot",1,0,null,50);clock.Ticks+=15000;history.Record("boot",1,0,null,50);
        var point=Assert.Single(history.Read());Assert.Equal(TimeSpan.FromSeconds(15),point.Age);Assert.Equal(0,point.Cpu);Assert.Null(point.Gpu);
    }
    [Fact] public void FailureAndRestartBreakTheLineWithoutInventingZero()
    {
        var clock=new Clock();var history=new MetricHistory(clock);
        history.Record("a",1,70,80,40);clock.Ticks+=2000;history.Gap();history.Gap();
        clock.Ticks+=2000;history.Record("a",2,40,30,45);clock.Ticks+=2000;history.Record("b",1,10,null,50);
        var points=history.Read();Assert.Equal(5,points.Count);
        Assert.Null(points[1].Cpu);Assert.Null(points[1].Gpu);Assert.Null(points[3].Cpu);
        Assert.Equal(10,points[4].Cpu);Assert.Null(points[4].Gpu);
    }
    [Fact] public async Task MonitorHistoryIsFreshBoundedAndIsolatedByTailnet()
    {
        var clock=new Clock();var sample=MonitorTests.Sample();
        using var client=new HttpClient(new MonitorTests.Handler((_,_)=>Task.FromResult(MonitorTests.Reply(sample))));
        using var monitor=new TailwatchMonitor(client,new(),clock);
        monitor.SetDirectory(MonitorTests.Directory());await monitor.PollAsync();clock.Ticks+=2000;
        sample=sample with {Sequence=2};await monitor.PollAsync();
        Assert.Equal(2,Assert.Single(monitor.Machines).History!.Count);
        clock.Ticks+=11000;var stale=Assert.Single(monitor.Machines);Assert.Equal("Stale",stale.Status);Assert.Null(stale.History![^1].Cpu);
        monitor.SetMonitored("n1",false);Assert.Empty(Assert.Single(monitor.Machines).History!);
        monitor.SetDirectory(MonitorTests.Directory("another"));Assert.Empty(Assert.Single(monitor.Machines).History!);
    }
    [Fact] public async Task HistoryPreservesMissingGpuAndFifteenSecondPolling()
    {
        var clock=new Clock();ulong sequence=0;
        using var client=new HttpClient(new MonitorTests.Handler((_,_)=>Task.FromResult(MonitorTests.Reply(MonitorTests.Sample() with {Sequence=++sequence,Gpus=[]} ))));
        using var monitor=new TailwatchMonitor(client,new(),clock);monitor.SetDirectory(MonitorTests.Directory());
        await monitor.PollAsync();clock.Ticks+=15000;await monitor.PollAsync();
        var history=Assert.Single(monitor.Machines).History!;Assert.Equal(2,history.Count);Assert.Equal(TimeSpan.FromSeconds(15),history[0].Age);
        Assert.All(history,p=>{Assert.Equal(0,p.Cpu);Assert.Null(p.Gpu);});
    }
    [Fact] public async Task BriefDiscoveryFailureLeavesAGapEvenBeforeReadingsExpire()
    {
        var clock=new Clock();ulong sequence=0;
        using var client=new HttpClient(new MonitorTests.Handler((_,_)=>Task.FromResult(MonitorTests.Reply(MonitorTests.Sample() with {Sequence=++sequence}))));
        using var monitor=new TailwatchMonitor(client,new(),clock);monitor.SetDirectory(MonitorTests.Directory());
        await monitor.PollAsync();clock.Ticks+=1000;monitor.DiscoveryFailed("Temporary network failure");clock.Ticks+=1000;
        monitor.SetDirectory(MonitorTests.Directory());await monitor.PollAsync();
        var history=Assert.Single(monitor.Machines).History!;Assert.Equal(3,history.Count);Assert.Null(history[1].Cpu);Assert.Equal(0,history[2].Cpu);
    }
}
