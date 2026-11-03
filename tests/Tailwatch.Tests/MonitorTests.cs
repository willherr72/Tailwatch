using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Tailwatch.Core;
using Xunit;
namespace Tailwatch.Tests;

public class MonitorTests
{
    public static Peer Peer(string id="n1")=>new(id,id,"linux",[IPAddress.Parse("100.64.0.2")],true);
    public static DirectorySnapshot Directory(string key="a",string id="n1")=>new(key,key,Peer(id),[Peer(id)]);
    public static Snapshot Sample(string id="n1")=>new(){NodeId=id,BootId="boot",Sequence=1,IntervalMs=2000,Cpu=new(){Value=0,Status="ok"},Ram=new(512,1024,"ok"),Gpus=[new("g1","GPU","NVIDIA",new(24,"ok"),"active_time")]};
    public static HttpResponseMessage Reply(Snapshot sample)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(sample,Protocol.Json),System.Text.Encoding.UTF8,"application/json")};
    public sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request,token);
    }
    [Fact] public async Task ReportsZeroCpuAndAvailableGpu()
    {
        using var client=new HttpClient(new Handler((_,_)=>Task.FromResult(Reply(Sample()))));
        using var monitor=new TailwatchMonitor(client,new());
        monitor.SetDirectory(Directory());await monitor.PollAsync();
        var row=Assert.Single(monitor.Machines);
        Assert.Equal("Live",row.Status);Assert.Equal(0,row.Cpu);Assert.Equal(24,row.Gpu);Assert.Equal(50,row.Ram);
    }
    [Fact] public async Task MissingAgentDoesNotBlockHealthyPeer()
    {
        using var client=new HttpClient(new Handler((r,_)=>r.RequestUri!.Host=="100.64.0.2"?Task.FromResult(Reply(Sample())):Task.FromException<HttpResponseMessage>(new HttpRequestException("refused"))));
        using var monitor=new TailwatchMonitor(client,new());
        monitor.SetDirectory(Directory() with {Peers=[Peer(),Peer("n2") with {Addresses=[IPAddress.Parse("100.64.0.3")]}]});
        await monitor.PollAsync();
        Assert.Equal("Live",monitor.Machines.Single(m=>m.Peer.Id=="n1").Status);
        Assert.Equal("Agent unavailable",monitor.Machines.Single(m=>m.Peer.Id=="n2").Status);
    }
    [Fact] public async Task OldTailnetResponseIsDiscarded()
    {
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client=new HttpClient(new Handler((_,_)=>{started.TrySetResult();return response.Task;}));
        using var monitor=new TailwatchMonitor(client,new());
        monitor.SetDirectory(Directory());
        var poll=monitor.PollAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        monitor.SetDirectory(Directory("b","n2"));
        response.SetResult(Reply(Sample()));
        await poll;
        Assert.Equal("b",monitor.NetworkName);
        var row=Assert.Single(monitor.Machines);Assert.Equal("n2",row.Peer.Id);Assert.Null(row.Snapshot);
    }
    [Fact] public async Task HTMLAndOversizedResponsesAreRejected()
    {
        foreach(var content in new[]{"<html>not an agent</html>",new string('x',65537)})
        {
            using var client=new HttpClient(new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(content)})));
            using var monitor=new TailwatchMonitor(client,new());monitor.SetDirectory(Directory());await monitor.PollAsync();
            Assert.Null(Assert.Single(monitor.Machines).Cpu);
            Assert.NotEqual("Live",monitor.Machines[0].Status);
        }
    }
    [Fact] public async Task StaleCollectorDoesNotShowLiveValues()
    {
        using var client=new HttpClient(new Handler((_,_)=>Task.FromResult(Reply(Sample() with {AgeMs=12000}))));
        using var monitor=new TailwatchMonitor(client,new());monitor.SetDirectory(Directory());await monitor.PollAsync();
        Assert.Equal("Stale",Assert.Single(monitor.Machines).Status);Assert.Null(monitor.Machines[0].Cpu);
    }
    [Fact] public void SelectionsAreSeparatePerNetwork()
    {
        var settings=new AppSettings();
        using var client=new HttpClient();
        using var monitor=new TailwatchMonitor(client,settings);
        monitor.SetDirectory(Directory());monitor.SetMonitored("n1",false);
        monitor.SetDirectory(Directory("b"));Assert.True(Assert.Single(monitor.Machines).Monitored);
        monitor.SetDirectory(Directory());Assert.False(Assert.Single(monitor.Machines).Monitored);
    }
}

