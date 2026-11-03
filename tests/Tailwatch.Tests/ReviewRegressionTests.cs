using System.Net;
using Tailwatch.Core;
using Xunit;
namespace Tailwatch.Tests;

public class ReviewRegressionTests
{
    sealed class Clock:TimeProvider
    {
        public long Ticks=1;
        public override long TimestampFrequency=>1000;
        public override long GetTimestamp()=>Ticks;
    }
    [Fact] public async Task TransientDiscoveryRetainsRowsAndAgesUntilRecovery()
    {
        var clock=new Clock();int requests=0;
        using var client=new HttpClient(new MonitorTests.Handler((_,_)=>{requests++;return Task.FromResult(MonitorTests.Reply(MonitorTests.Sample()));}));
        using var monitor=new TailwatchMonitor(client,new(),clock);
        monitor.SetDirectory(MonitorTests.Directory());await monitor.PollAsync();
        monitor.DiscoveryFailed("Temporary discovery failure");
        Assert.Equal("a",monitor.NetworkKey);Assert.Single(monitor.Machines);
        await monitor.PollAsync();Assert.Equal(1,requests);
        clock.Ticks+=11000;Assert.Equal("Stale",monitor.Machines[0].Status);
        Assert.Null(monitor.Machines[0].Cpu);
        monitor.SetDirectory(MonitorTests.Directory());await monitor.PollAsync();
        Assert.Null(monitor.Error);Assert.Equal(2,requests);
    }
    [Fact] public void ExplicitDisconnectDiffersFromMalformedDirectory()
    {
        Assert.True(Assert.Throws<ProtocolException>(()=>Tailscale.Parse("{\"BackendState\":\"Stopped\"}")).Disconnected);
        Assert.False(Assert.Throws<ProtocolException>(()=>Tailscale.Parse("{}")).Disconnected);
    }
    [Fact] public async Task DiscoveryFailureDiscardsPendingResponseAndPreventsOverlap()
    {
        int requests=0;
        var pending=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client=new HttpClient(new MonitorTests.Handler((_,_)=>++requests==1?pending.Task:Task.FromResult(MonitorTests.Reply(MonitorTests.Sample()))));
        using var monitor=new TailwatchMonitor(client,new());monitor.SetDirectory(MonitorTests.Directory());
        var first=monitor.PollAsync();await monitor.PollAsync();Assert.Equal(1,requests);
        monitor.DiscoveryFailed("Temporary failure");
        pending.SetResult(MonitorTests.Reply(MonitorTests.Sample()));await first;
        Assert.Single(monitor.Machines);Assert.Null(monitor.Machines[0].Snapshot);
        monitor.SetDirectory(MonitorTests.Directory());await monitor.PollAsync();
        Assert.Equal("Live",monitor.Machines[0].Status);Assert.Equal(2,requests);
    }
    [Fact] public async Task Html503IsUnavailableAndBacksOff()
    {
        int requests=0;
        using var client=new HttpClient(new MonitorTests.Handler((_,_)=>{requests++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable){Content=new StringContent("<html>maintenance</html>")});}));
        using var monitor=new TailwatchMonitor(client,new());monitor.SetDirectory(MonitorTests.Directory());
        await monitor.PollAsync();await monitor.PollAsync();
        Assert.Equal("Agent unavailable",monitor.Machines[0].Status);Assert.Equal(1,requests);
    }
    [Fact] public async Task PersistentWarmupHasBoundedGracePeriod()
    {
        var clock=new Clock();int requests=0;
        using var client=new HttpClient(new MonitorTests.Handler((_,_)=>{requests++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable){Content=new StringContent("warming_up")});}));
        using var monitor=new TailwatchMonitor(client,new(),clock);monitor.SetDirectory(MonitorTests.Directory());
        await monitor.PollAsync();Assert.Equal("Connecting",monitor.Machines[0].Status);
        clock.Ticks+=11000;await monitor.PollAsync();await monitor.PollAsync();
        Assert.Equal("Agent unavailable",monitor.Machines[0].Status);Assert.Equal(2,requests);
    }
    [Fact] public async Task SlowFleetDoesNotQueueRoundsOrStarveHealthyPeer()
    {
        int healthy=0,active=0,maximum=0;
        var peers=new List<Peer>{MonitorTests.Peer()};
        peers.AddRange(Enumerable.Range(3,48).Select(n=>MonitorTests.Peer("n"+n) with {Addresses=[IPAddress.Parse("100.64.0."+n)]}));
        using var stop=new CancellationTokenSource();
        using var client=new HttpClient(new MonitorTests.Handler(async(request,token)=>
        {
            int count=Interlocked.Increment(ref active);maximum=Math.Max(maximum,count);
            try
            {
                if(request.RequestUri!.Host=="100.64.0.2")return MonitorTests.Reply(MonitorTests.Sample() with {Sequence=(ulong)Interlocked.Increment(ref healthy)});
                await Task.Delay(400,token);throw new HttpRequestException("timeout");
            }
            finally{Interlocked.Decrement(ref active);}
        }));
        using var monitor=new TailwatchMonitor(client,new());
        monitor.SetDirectory(MonitorTests.Directory() with {Peers=peers});
        var rounds=new List<Task>();
        try
        {
            rounds.Add(monitor.PollAsync(token:stop.Token));
            for(int i=0;i<12;i++){await Task.Delay(100);rounds.Add(monitor.PollAsync(token:stop.Token));}
            Assert.True(healthy>=3,$"Only {healthy} healthy samples while slow peers were pending.");
            Assert.True(rounds[0].IsCompleted,"The first round queued the entire slow fleet.");
            Assert.InRange(maximum,1,8);Assert.Equal("Live",monitor.Machines[0].Status);
        }
        finally{stop.Cancel();await Task.WhenAll(rounds);}
    }
}
