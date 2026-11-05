using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
namespace Tailwatch.Core;

public sealed record LocalAgentOptions
{
    public int Port {get;init;}=48173;
    public TimeSpan PollInterval {get;init;}=TimeSpan.FromSeconds(2);
    public TimeSpan RequestTimeout {get;init;}=TimeSpan.FromSeconds(2);
    public TimeSpan UnresponsiveDeadline {get;init;}=TimeSpan.FromSeconds(20);
    public TimeSpan InitialRetryDelay {get;init;}=TimeSpan.FromSeconds(2);
    public TimeSpan MaximumRetryDelay {get;init;}=TimeSpan.FromSeconds(60);
    public TimeSpan HealthyResetInterval {get;init;}=TimeSpan.FromMinutes(1);
}

public sealed class LocalAgentSupervisor(Func<ProcessStartInfo> startInfo,LocalAgentOptions? options=null,Action<string>? log=null):IAsyncDisposable
{
    readonly object sync=new();
    readonly CancellationTokenSource lifetime=new();
    readonly LocalAgentOptions config=options??new();
    Task? run;
    bool stopping,disposed;
    volatile bool networkAvailable=true;
    int processId;
    string status="Stopped";
    public string Status=>Volatile.Read(ref status);
    public int? OwnedProcessId=>Volatile.Read(ref processId) is var id&&id!=0?id:null;

    // The run loop alone owns the Process. Start/Stop only control its lifetime;
    // no UI continuation can race a new process against cleanup.
    public void Start()
    {
        lock(sync)
        {
            if(stopping||run!=null)return;
            if(config.Port is <1 or >65535||config.PollInterval<=TimeSpan.Zero||config.RequestTimeout<=TimeSpan.Zero||
                config.UnresponsiveDeadline<=TimeSpan.Zero||config.InitialRetryDelay<=TimeSpan.Zero||
                config.MaximumRetryDelay<config.InitialRetryDelay||config.HealthyResetInterval<=TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options));
            var token=lifetime.Token;
            run=Task.Run(()=>RunAsync(token));
        }
    }
    public void SetNetworkAvailable(bool available)=>networkAvailable=available;
    public Task StopAsync()
    {
        lock(sync)
        {
            if(!stopping){stopping=true;lifetime.Cancel();}
            return run??Task.CompletedTask;
        }
    }
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock(sync){if(disposed)return;disposed=true;lifetime.Dispose();}
    }
    void Report(string message)
    {
        if(Status==message)return;
        Volatile.Write(ref status,message);log?.Invoke(message);
    }
    bool PortOccupied()
    {
        // Include listeners which accept TCP but never answer HTTP. Enumerating
        // listeners also avoids consuming an external service's connection slots.
        try{return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e=>e.Port==config.Port&&
            (IPAddress.IsLoopback(e.Address)||e.Address.Equals(IPAddress.Any)||e.Address.Equals(IPAddress.IPv6Any)));}
        catch(NetworkInformationException){return true;}
    }
    async Task<bool> RespondingAsync(HttpClient http,CancellationToken token)
    {
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(token);budget.CancelAfter(config.RequestTimeout);
        try
        {
            using var response=await http.GetAsync($"http://127.0.0.1:{config.Port}/v1/health",HttpCompletionOption.ResponseHeadersRead,budget.Token).ConfigureAwait(false);
            if(response.IsSuccessStatusCode)return true;
            // Network identity transitions are not sampler failures. A responsive
            // HTTP server with a dead sampler must still pass through the deadline.
            if(response.StatusCode!=HttpStatusCode.ServiceUnavailable||
                response.Content.Headers.ContentType?.MediaType!="text/plain"||
                response.Content.Headers.ContentLength>64)return false;
            await using var body=await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
            var bytes=new byte[65];int count=0;
            while(count<bytes.Length)
            {
                int read=await body.ReadAsync(bytes.AsMemory(count),budget.Token).ConfigureAwait(false);
                if(read==0)break;
                count+=read;
            }
            return count<=64&&System.Text.Encoding.UTF8.GetString(bytes,0,count)=="tailscale_unavailable";
        }
        catch(Exception e)when(e is HttpRequestException or IOException){return false;}
        catch(OperationCanceledException)when(!token.IsCancellationRequested){return false;}
    }
    static async Task DrainAsync(StreamReader reader,CancellationToken token)
    {
        // Never retain unbounded lines or relay potentially private inventories.
        var buffer=new char[1024];
        try{while(await reader.ReadAsync(buffer.AsMemory(),token).ConfigureAwait(false)!=0){}}
        catch(Exception e)when(e is IOException or OperationCanceledException or ObjectDisposedException){}
    }
    static async Task<bool> ReapAsync(Process child)
    {
        try
        {
            if(!child.HasExited)child.Kill(entireProcessTree:true);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false);return true;
        }
        catch(InvalidOperationException){return true;}
        catch(Exception e)when(e is System.ComponentModel.Win32Exception or OperationCanceledException){return false;}
    }
    async Task RunAsync(CancellationToken token)
    {
        Process? child=null;Task drain=Task.CompletedTask;
        var retry=config.InitialRetryDelay;
        using var http=new HttpClient(new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false}){Timeout=Timeout.InfiniteTimeSpan};
        try
        {
            while(!token.IsCancellationRequested)
            {
                if(!networkAvailable)
                {
                    Report("Waiting for Tailscale before starting local agent");
                    await Task.Delay(config.PollInterval,token).ConfigureAwait(false);continue;
                }
                if(PortOccupied())
                {
                    Report("Using externally managed local agent");
                    // Ownership stays external for this viewer lifetime, even if
                    // its service temporarily releases the port during a restart.
                    await Task.Delay(Timeout.InfiniteTimeSpan,token).ConfigureAwait(false);return;
                }
                token.ThrowIfCancellationRequested();
                try
                {
                    var start=startInfo();
                    token.ThrowIfCancellationRequested();
                    start.UseShellExecute=false;start.CreateNoWindow=true;start.WindowStyle=ProcessWindowStyle.Hidden;
                    start.RedirectStandardError=true;start.RedirectStandardOutput=false;
                    child=Process.Start(start)??throw new IOException("Process did not start");
                    Volatile.Write(ref processId,child.Id);
                    drain=DrainAsync(child.StandardError,token);
                    Report("Starting local agent");
                    var lastResponse=Stopwatch.GetTimestamp();long? healthySince=null;
                    while(!token.IsCancellationRequested)
                    {
                        if(child.HasExited){Report($"Local agent exited (code {child.ExitCode}); retrying");break;}
                        if(!networkAvailable)
                        {
                            lastResponse=Stopwatch.GetTimestamp();healthySince=null;
                            Report("Local agent waiting for Tailscale");
                        }
                        else if(await RespondingAsync(http,token).ConfigureAwait(false))
                        {
                            lastResponse=Stopwatch.GetTimestamp();healthySince??=lastResponse;
                            if(Stopwatch.GetElapsedTime(healthySince.Value)>=config.HealthyResetInterval)retry=config.InitialRetryDelay;
                            Report("Local agent responding");
                        }
                        else
                        {
                            healthySince=null;
                            // Discovery can disconnect while the health request is
                            // awaiting its deadline. Do not kill the helper for that.
                            if(!networkAvailable){lastResponse=Stopwatch.GetTimestamp();Report("Local agent waiting for Tailscale");}
                            else if(Stopwatch.GetElapsedTime(lastResponse)>=config.UnresponsiveDeadline){Report("Local agent health check failed; restarting");break;}
                        }
                        await Task.Delay(config.PollInterval,token).ConfigureAwait(false);
                    }
                }
                catch(Exception e)when(e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
                {Report("Local agent could not start; retrying");}
                if(child!=null)
                {
                    while(!await ReapAsync(child).ConfigureAwait(false))
                    {
                        Report("Could not stop owned local agent; retrying cleanup");
                        await Task.Delay(config.MaximumRetryDelay,token).ConfigureAwait(false);
                    }
                    await drain.ConfigureAwait(false);child.Dispose();child=null;Volatile.Write(ref processId,0);
                }
                await Task.Delay(retry,token).ConfigureAwait(false);
                retry=TimeSpan.FromMilliseconds(Math.Min(config.MaximumRetryDelay.TotalMilliseconds,retry.TotalMilliseconds*2));
            }
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
        finally
        {
            if(child!=null)
            {
                if(!await ReapAsync(child).ConfigureAwait(false))Report("Could not stop owned local agent during shutdown");
                await drain.ConfigureAwait(false);child.Dispose();Volatile.Write(ref processId,0);
            }
            Report("Stopped");
        }
    }
}
