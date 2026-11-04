using System.Net;
using System.Text.Json;
using Tailwatch.Core;
using Xunit;
namespace Tailwatch.Tests;
public class RecoveryTests
{
    [Fact] public async Task RepeatedDisposeCancelsPendingWorkAndRejectsLateResponses()
    {
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client=new HttpClient(new MonitorTests.Handler((_,_)=>{started.TrySetResult();return response.Task;}));
        var monitor=new TailwatchMonitor(client,new());monitor.SetDirectory(MonitorTests.Directory());
        var poll=monitor.PollAsync();await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        monitor.Dispose();monitor.Dispose();
        response.SetResult(MonitorTests.Reply(MonitorTests.Sample()));await poll;
        await monitor.PollAsync(true);monitor.SetDirectory(MonitorTests.Directory("late"));
        Assert.DoesNotContain(monitor.Machines,m=>m.Status=="Live");
    }
    [Theory] [InlineData("denied","Access denied")] [InlineData("refused","Could not connect to agent")] [InlineData("timeout","Agent request timed out")]
    public async Task CachedSnapshotPreservesFailureReasonAndRecoveryClearsIt(string failure,string expected)
    {
        bool fail=false;
        using var client=new HttpClient(new MonitorTests.Handler(async(_,token)=>
        {
            if(!fail)return MonitorTests.Reply(MonitorTests.Sample());
            if(failure=="denied")return new(HttpStatusCode.Forbidden);
            if(failure=="refused")throw new HttpRequestException("refused");
            await Task.Delay(Timeout.Infinite,token);throw new Exception("unreachable");
        }));
        using var monitor=new TailwatchMonitor(client,new());monitor.SetDirectory(MonitorTests.Directory());
        await monitor.PollAsync();var success=monitor.Machines[0].LastSuccess;
        fail=true;await monitor.PollAsync(true);var row=monitor.Machines[0];
        Assert.Equal("Stale",row.Status);Assert.Null(row.Cpu);Assert.Equal(success,row.LastSuccess);
        Assert.Equal(expected,row.FailureReason);
        fail=false;await monitor.PollAsync(true);Assert.Equal("Live",monitor.Machines[0].Status);Assert.Null(monitor.Machines[0].FailureReason);
    }
    sealed class Clock:TimeProvider
    {
        public long Ticks=1;
        public override long TimestampFrequency=>1000;
        public override long GetTimestamp()=>Ticks;
        public override DateTimeOffset GetUtcNow()=>DateTimeOffset.UnixEpoch.AddMilliseconds(Ticks);
    }
    [Fact] public async Task ExpandedGpuReadingAgesAlongsideSummary()
    {
        var clock=new Clock();var s=MonitorTests.Sample();
        s=s with {Gpus=[s.Gpus[0] with {Utilization=new Reading(50,"ok",9000)}]};
        using var client=new HttpClient(new MonitorTests.Handler((_,_)=>Task.FromResult(MonitorTests.Reply(s))));
        using var monitor=new TailwatchMonitor(client,new(),clock);
        monitor.SetDirectory(MonitorTests.Directory());await monitor.PollAsync();
        clock.Ticks+=1500;
        var row=Assert.Single(monitor.Machines);
        Assert.Null(row.Gpu);
        Assert.False(row.Snapshot!.Gpus[0].Utilization.IsFresh);
    }
    [Fact] public async Task RepeatedSequenceBecomesStaleAndRebootRecovers()
    {
        var clock=new Clock();var s=MonitorTests.Sample();
        using var client=new HttpClient(new MonitorTests.Handler((_,_)=>Task.FromResult(MonitorTests.Reply(s))));
        using var monitor=new TailwatchMonitor(client,new(),clock);
        monitor.SetDirectory(MonitorTests.Directory());await monitor.PollAsync();
        clock.Ticks+=11000;await monitor.PollAsync(true);
        Assert.Equal("Stale",monitor.Machines[0].Status);
        Assert.Equal("Agent sample sequence stopped advancing",monitor.Machines[0].FailureReason);
        s=s with {BootId="new-boot"};await monitor.PollAsync(true);
        Assert.Equal("Live",monitor.Machines[0].Status);
        Assert.Null(monitor.Machines[0].FailureReason);
    }
    [Fact] public void CustomPortsPersistWithProfile()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json");
        try
        {
            var settings=new AppSettings();settings.For("a").Ports["n1"]=49000;settings.Save(path);
            using var client=new HttpClient();using var monitor=new TailwatchMonitor(client,AppSettings.Load(path));
            monitor.SetDirectory(MonitorTests.Directory());Assert.Equal(49000,monitor.GetPort("n1"));
            monitor.SetDirectory(MonitorTests.Directory("b"));Assert.Equal(48173,monitor.GetPort("n1"));
        }
        finally{File.Delete(path);}
    }
    [Fact] public void InvalidProtocolVersionAndNullMetricsAreRejected()
    {
        foreach(var sample in new[]{MonitorTests.Sample() with {SchemaVersion=2},MonitorTests.Sample() with {Ram=new(null,100,"ok")},MonitorTests.Sample() with {Cpu=new(){Value=null,Status="ok"}}})
            Assert.Throws<ProtocolException>(()=>Protocol.Parse(JsonSerializer.SerializeToUtf8Bytes(sample,Protocol.Json),"n1"));
    }
    [Fact] public async Task DisablingMachineDropsPendingResponse()
    {
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client=new HttpClient(new MonitorTests.Handler((_,_)=>{started.SetResult();return pending.Task;}));
        using var monitor=new TailwatchMonitor(client,new());monitor.SetDirectory(MonitorTests.Directory());
        var work=monitor.PollAsync();await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        monitor.SetMonitored("n1",false);pending.SetResult(MonitorTests.Reply(MonitorTests.Sample()));await work;
        Assert.Equal("Not monitored",monitor.Machines[0].Status);Assert.Null(monitor.Machines[0].Snapshot);
    }
    [Fact] public async Task RefusedPeerBacksOffUntilExplicitRefresh()
    {
        int requests=0;
        using var client=new HttpClient(new MonitorTests.Handler((_,_)=>{requests++;return Task.FromException<HttpResponseMessage>(new HttpRequestException());}));
        using var monitor=new TailwatchMonitor(client,new());monitor.SetDirectory(MonitorTests.Directory());
        await monitor.PollAsync();await monitor.PollAsync();Assert.Equal(1,requests);
        await monitor.PollAsync(true);Assert.Equal(2,requests);
    }
    [Theory] [InlineData("tailscale_unavailable")] [InlineData("old_identity")] [InlineData("refused")]
    public async Task LocalAgentRecoversOnNextTickAfterTailnetRoundTrip(string failure)
    {
        var clock=new Clock();string agentId="n1";bool failing=false;
        using var client=new HttpClient(new MonitorTests.Handler((request,_)=>
        {
            Assert.Equal("127.0.0.1",request.RequestUri!.Host);
            if(!failing)return Task.FromResult(MonitorTests.Reply(MonitorTests.Sample(agentId)));
            return failure switch
            {
                "tailscale_unavailable"=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable){Content=new StringContent("tailscale_unavailable")}),
                "old_identity"=>Task.FromResult(MonitorTests.Reply(MonitorTests.Sample("previous-node"))),
                _=>Task.FromException<HttpResponseMessage>(new HttpRequestException("refused"))
            };
        }));
        using var monitor=new TailwatchMonitor(client,new(),clock);
        foreach(var (key,id) in new[]{("home","n1"),("work","n2"),("home","n1")})
        {
            var self=MonitorTests.Peer(id) with {IsSelf=true};
            monitor.SetDirectory(new(key,key,self,[self]));agentId=id;failing=true;
            await monitor.PollAsync();
            Assert.Null(Assert.Single(monitor.Machines).Snapshot);
            failing=false;clock.Ticks+=2000;
            await monitor.PollAsync(); // Normal timer poll, without forcing Refresh.
            var row=Assert.Single(monitor.Machines);Assert.Equal("Live",row.Status);Assert.Equal(id,row.Snapshot!.NodeId);
        }
    }
    [Fact] public async Task LocalTailnetTransitionHasBoundedConnectingGrace()
    {
        var clock=new Clock();
        using var client=new HttpClient(new MonitorTests.Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable){Content=new StringContent("tailscale_unavailable")})));
        using var monitor=new TailwatchMonitor(client,new(),clock);
        var self=MonitorTests.Peer() with {IsSelf=true};monitor.SetDirectory(new("home","home",self,[self]));
        await monitor.PollAsync();Assert.Equal("Connecting",monitor.Machines[0].Status);
        clock.Ticks+=11000;await monitor.PollAsync();Assert.Equal("Agent unavailable",monitor.Machines[0].Status);
    }
}
