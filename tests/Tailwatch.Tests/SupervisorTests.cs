using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Tailwatch.Core;
using Xunit;
namespace Tailwatch.Tests;

public class SupervisorTests
{
    static int FreePort(){var socket=new TcpListener(IPAddress.Loopback,0);socket.Start();int port=((IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();return port;}
    static ProcessStartInfo Child(int port,string mode)
    {
        var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"fixture","Tailwatch.AgentFixture.exe"));
        start.Environment["DOTNET_ROOT"]=Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),"../../.."));
        start.ArgumentList.Add(port.ToString());start.ArgumentList.Add(mode);return start;
    }
    static LocalAgentOptions Options(int port)=>new(){Port=port,PollInterval=TimeSpan.FromMilliseconds(50),RequestTimeout=TimeSpan.FromMilliseconds(150),UnresponsiveDeadline=TimeSpan.FromMilliseconds(650),InitialRetryDelay=TimeSpan.FromMilliseconds(150),MaximumRetryDelay=TimeSpan.FromMilliseconds(600),HealthyResetInterval=TimeSpan.FromSeconds(3)};
    static async Task Until(Func<bool> condition)
    {
        var deadline=Stopwatch.StartNew();
        while(!condition()){if(deadline.Elapsed>TimeSpan.FromSeconds(12))throw new TimeoutException("Supervisor condition not reached");await Task.Delay(25);}
    }
    static bool Alive(int pid){try{using var process=Process.GetProcessById(pid);return !process.HasExited;}catch(ArgumentException){return false;}}
    [Fact] public async Task OwnedProcessExitRestartsAndConcurrentStartNeverDuplicates()
    {
        int port=FreePort(),starts=0;
        await using var supervisor=new LocalAgentSupervisor(()=>{Interlocked.Increment(ref starts);return Child(port,"healthy");},Options(port));
        Parallel.For(0,10,_=>supervisor.Start());
        await Until(()=>supervisor.Status=="Local agent responding");int first=supervisor.OwnedProcessId!.Value;
        using(var process=Process.GetProcessById(first)){process.Kill(true);await process.WaitForExitAsync();}
        await Until(()=>supervisor.Status=="Local agent responding"&&supervisor.OwnedProcessId!=first);
        int second=supervisor.OwnedProcessId!.Value;Assert.Equal(2,starts);
        await supervisor.StopAsync();await supervisor.StopAsync();Assert.False(Alive(second));
        supervisor.Start();await Task.Delay(250);Assert.Equal(2,starts);
    }
    [Theory] [InlineData("hang")] [InlineData("waiting")]
    public async Task UnresponsiveOwnedChildIsReapedAndReplaced(string mode)
    {
        int port=FreePort(),starts=0;int? first=null;
        await using var supervisor=new LocalAgentSupervisor(()=>Child(port,Interlocked.Increment(ref starts)==1?mode:"healthy"),Options(port));
        supervisor.Start();await Until(()=>supervisor.OwnedProcessId.HasValue);first=supervisor.OwnedProcessId;
        await Until(()=>supervisor.Status=="Local agent responding"&&starts==2);
        Assert.False(Alive(first!.Value));
    }
    [Fact] public async Task Responsive503AndDisconnectedStartupDoNotTriggerRestarts()
    {
        foreach(var mode in new[]{"unavailable","waiting"})
        {
            int port=FreePort(),starts=0;
            await using var supervisor=new LocalAgentSupervisor(()=>{starts++;return Child(port,mode);},Options(port));
            supervisor.Start();await Until(()=>supervisor.OwnedProcessId.HasValue);int pid=supervisor.OwnedProcessId!.Value;
            if(mode=="waiting")supervisor.SetNetworkAvailable(false);
            else await Until(()=>supervisor.Status=="Local agent responding");
            await Task.Delay(1600);Assert.Equal(1,starts);Assert.True(Alive(pid));
        }
    }
    [Theory] [InlineData("sampler-failed")] [InlineData("health-body-hang")] [InlineData("health-oversized")]
    public async Task UnhealthySamplerIsReapedAndReplaced(string mode)
    {
        int port=FreePort(),starts=0;
        await using var supervisor=new LocalAgentSupervisor(()=>Child(port,Interlocked.Increment(ref starts)==1?mode:"healthy"),Options(port));
        supervisor.Start();await Until(()=>supervisor.OwnedProcessId.HasValue);int first=supervisor.OwnedProcessId!.Value;
        await Until(()=>supervisor.Status=="Local agent responding"&&starts==2);
        Assert.False(Alive(first));Assert.NotEqual(first,supervisor.OwnedProcessId);
    }
    [Fact] public async Task DisconnectDuringHealthRequestDoesNotKillOwnedHelper()
    {
        int port=FreePort();var marker=Path.Combine(Path.GetTempPath(),"tailwatch-health-"+Guid.NewGuid());
        await using var supervisor=new LocalAgentSupervisor(()=>{var start=Child(port,"hang");start.ArgumentList.Add(marker);return start;},
            Options(port) with {RequestTimeout=TimeSpan.FromMilliseconds(800)});
        try
        {
            supervisor.Start();await Until(()=>File.Exists(marker));int first=supervisor.OwnedProcessId!.Value;
            supervisor.SetNetworkAvailable(false);await Task.Delay(1200);
            Assert.True(Alive(first));Assert.Equal(first,supervisor.OwnedProcessId);
        }
        finally{File.Delete(marker);}
    }
    [Theory] [InlineData("exit")] [InlineData("flap")] [InlineData("missing")]
    public async Task RepeatedFailuresHaveCappedExponentialRetryRate(string mode)
    {
        int port=FreePort();var starts=new ConcurrentQueue<long>();var time=Stopwatch.StartNew();
        await using var supervisor=new LocalAgentSupervisor(()=>{starts.Enqueue(time.ElapsedMilliseconds);return mode=="missing"?new ProcessStartInfo(Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".exe")):Child(port,mode);},Options(port));
        supervisor.Start();await Until(()=>starts.Count>=5);await supervisor.StopAsync();var times=starts.ToArray();
        Assert.InRange(times.Length,5,6);
        for(int i=1;i<5;i++)Assert.True(times[i]-times[i-1]>=new[]{0,150,300,600,600}[i]-20,$"Retry {i}: {times[i]-times[i-1]}ms");
    }
    [Fact] public async Task ShutdownDuringLaunchReapsChildAndPreventsLateRestart()
    {
        int port=FreePort(),starts=0;using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        await using var supervisor=new LocalAgentSupervisor(()=>{starts++;entered.Set();release.Wait(TimeSpan.FromSeconds(5));return Child(port,"healthy");},Options(port));
        supervisor.Start();Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var stop=supervisor.StopAsync();release.Set();await stop.WaitAsync(TimeSpan.FromSeconds(5));await supervisor.DisposeAsync();
        Assert.Null(supervisor.OwnedProcessId);supervisor.Start();await Task.Delay(250);Assert.Equal(1,starts);
        using var check=new TcpClient();await Assert.ThrowsAsync<SocketException>(async()=>await check.ConnectAsync(IPAddress.Loopback,port));
    }
    [Fact] public async Task ExternalListenerIsNeverStartedOverOrStopped()
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;int starts=0;
        try
        {
            await using var supervisor=new LocalAgentSupervisor(()=>{starts++;return Child(port,"healthy");},Options(port));
            supervisor.Start();await Until(()=>supervisor.Status=="Using externally managed local agent");
            await Task.Delay(300);Assert.Equal(0,starts);await supervisor.StopAsync();
            using var check=new TcpClient();await check.ConnectAsync(IPAddress.Loopback,port);Assert.True(check.Connected);
        }
        finally{listener.Stop();}
    }
    [Fact] public async Task PreviouslyExternalListenerIsNotTakenOverDuringItsRestart()
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;int starts=0;
        try
        {
            await using var supervisor=new LocalAgentSupervisor(()=>{starts++;return Child(port,"healthy");},Options(port));
            supervisor.Start();await Until(()=>supervisor.Status=="Using externally managed local agent");listener.Stop();
            await Task.Delay(350);Assert.Equal(0,starts);
        }
        finally{listener.Stop();}
    }
}
