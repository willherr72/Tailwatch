using System.Net;
namespace Tailwatch.Core;
public record MachineState(Peer Peer,bool Monitored,string Status,Snapshot? Snapshot=null,DateTimeOffset? LastSuccess=null,double? Cpu=null,double? Gpu=null,double? Ram=null,bool PartialGpu=false);
public sealed class TailwatchMonitor : IDisposable
{
    sealed class Entry(Peer peer,bool monitored)
    {
        public Peer Peer=peer;
        public bool Monitored=monitored;
        public string Status=peer.Online?"Connecting":"Offline";
        public Snapshot? Snapshot;
        public DateTimeOffset? LastSuccess;
        public long Received,UnchangedSince,LastAttempt;
        public bool Attempted,Backoff;
    }
    readonly object sync=new();
    readonly HttpClient client;
    readonly AppSettings settings;
    readonly TimeProvider clock;
    readonly SemaphoreSlim round=new(1,1),slots=new(8,8);
    readonly Dictionary<string,Entry> entries=[];
    CancellationTokenSource scope=new();
    int generation;
    string key="",name="";
    string? error;
    public TailwatchMonitor(HttpClient client,AppSettings settings,TimeProvider? clock=null)
    {this.client=client;this.settings=settings;this.clock=clock??TimeProvider.System;}
    public string NetworkName {get{lock(sync)return name;}}
    public string NetworkKey {get{lock(sync)return key;}}
    public string? Error {get{lock(sync)return error;}}
    public IReadOnlyList<MachineState> Machines {get{lock(sync)return entries.Values.Select(View).ToList();}}
    double Age(long at)=>clock.GetElapsedTime(at).TotalMilliseconds;
    MachineState View(Entry e)
    {
        var s=e.Snapshot;
        var elapsed=s==null?0:Age(e.Received);
        var live=e.Monitored && e.Peer.Online && e.Status=="Live" && s!=null && s.AgeMs+elapsed<=10000 && Age(e.UnchangedSince)<=10000;
        var status=!e.Monitored?"Not monitored":!e.Peer.Online?"Offline":e.Status=="Live"&&!live?"Stale":e.Status;
        bool Fresh(Reading r)=>r.IsFresh && r.AgeMs+elapsed<=10000;
        var gpus=live?s!.Gpus.Where(g=>Fresh(g.Utilization)).ToList():[];
        double? gpu=gpus.Select(g=>g.Utilization.Value).DefaultIfEmpty(null).Max();
        long Aged(long age) => (long)Math.Min(long.MaxValue,age+elapsed);
        var display=s==null?null:s with {AgeMs=Aged(s.AgeMs),Cpu=s.Cpu with {AgeMs=Aged(s.Cpu.AgeMs)},Ram=s.Ram with {AgeMs=Aged(s.Ram.AgeMs)},
            Gpus=s.Gpus.Select(g=>g with {Utilization=g.Utilization with {AgeMs=Aged(g.Utilization.AgeMs)},Memory=g.Memory is null?null:g.Memory with {AgeMs=Aged(g.Memory.AgeMs)}}).ToList()};
        return new(e.Peer,e.Monitored,status,display,e.LastSuccess,
            live&&Fresh(s!.Cpu)?s.Cpu.Value:null,gpu,
            live&&s!.Ram.AgeMs+elapsed<=10000?s.Ram.Percentage:null,
            gpu!=null&&(s!.GpuStatus=="partial"||gpus.Count!=s.Gpus.Count));
    }
    void NewScope()
    {
        scope.Cancel();scope.Dispose();scope=new();generation++;entries.Clear();
    }
    public void SetDirectory(DirectorySnapshot directory)
    {
        lock(sync)
        {
            if(key!=directory.Key){NewScope();key=directory.Key;}
            name=directory.Name;error=null;
            var profile=settings.For(key);
            var present=directory.Peers.Select(p=>p.Id).ToHashSet();
            foreach(var id in entries.Keys.Where(id=>!present.Contains(id)).ToArray())entries.Remove(id);
            foreach(var peer in directory.Peers)
            {
                if(entries.TryGetValue(peer.Id,out var current))
                {
                    bool changed=!current.Peer.Addresses.SequenceEqual(peer.Addresses)||current.Peer.Online!=peer.Online;
                    current.Peer=peer;
                    if(changed){current.Snapshot=null;current.Status=peer.Online?"Connecting":"Offline";current.Backoff=false;}
                }
                else
                {
                    bool enabled=profile.Monitored.TryGetValue(peer.Id,out var choice)?choice:peer.Os is "windows" or "linux" or "macOS" or "macos" or "darwin";
                    entries[peer.Id]=new(peer,enabled);
                }
            }
        }
    }
    public void Disconnect(string message)
    {
        lock(sync){NewScope();key="";name="Tailscale disconnected";error=message;}
    }
    public void SetMonitored(string id,bool enabled)
    {
        lock(sync)if(entries.TryGetValue(id,out var e))
        {
            settings.For(key).Monitored[id]=enabled;e.Monitored=enabled;
            e.Snapshot=null;e.Backoff=false;e.Status=e.Peer.Online?"Connecting":"Offline";
        }
    }
    public void SetPort(string id,int port)
    {
        if(port is <1 or >65535)throw new ArgumentOutOfRangeException(nameof(port));
        lock(sync){settings.For(key).Ports[id]=port;if(entries.TryGetValue(id,out var e)){e.Backoff=false;e.Snapshot=null;e.Status="Connecting";}}
    }
    public int GetPort(string id) {lock(sync)return settings.For(key).Ports.GetValueOrDefault(id,48173);}
    public async Task PollAsync(bool force=false,CancellationToken token=default)
    {
        if(!await round.WaitAsync(0,token))return;
        try
        {
            List<(Peer Peer,int Port)> peers;
            int version;CancellationToken networkToken;
            lock(sync)
            {
                version=generation;networkToken=scope.Token;
                peers=entries.Values.Where(e=>e.Monitored&&e.Peer.Online&&(force||!e.Backoff||!e.Attempted||Age(e.LastAttempt)>=30000))
                    .Select(e=>(e.Peer,settings.For(key).Ports.GetValueOrDefault(e.Peer.Id,48173))).ToList();
            }
            await Task.WhenAll(peers.Select(p=>PollPeerAsync(p.Peer,p.Port,version,networkToken,token)));
        }
        finally{round.Release();}
    }
    async Task PollPeerAsync(Peer peer,int port,int version,CancellationToken networkToken,CancellationToken token)
    {
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(networkToken,token);
        try
        {
            await slots.WaitAsync(budget.Token);
            try
            {
                budget.CancelAfter(TimeSpan.FromSeconds(2));
                var address=peer.IsSelf?IPAddress.Loopback:peer.Addresses.FirstOrDefault(ip=>ip.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork)??peer.Addresses[0];
                var url=new UriBuilder("http",address.ToString(),port,"v1/snapshot").Uri;
                using var request=new HttpRequestMessage(HttpMethod.Get,url);
                using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,budget.Token);
                if(response.StatusCode==HttpStatusCode.Forbidden)throw new ProtocolException("Access denied");
                if(response.StatusCode==HttpStatusCode.ServiceUnavailable)
                {
                    // An agent that is starting may not have its first CPU sample yet.
                    UpdateFailure(peer,port,version,"Connecting",false);return;
                }
                if(!response.IsSuccessStatusCode)throw new ProtocolException("Agent unavailable");
                if(response.Content.Headers.ContentType?.MediaType!="application/json")throw new ProtocolException("Invalid agent response");
                if(response.Content.Headers.ContentLength>65536)throw new ProtocolException("Response too large");
                await using var input=await response.Content.ReadAsStreamAsync(budget.Token);
                var buffer=new byte[65537];var count=0;
                while(count<buffer.Length)
                {
                    var read=await input.ReadAsync(buffer.AsMemory(count),budget.Token);
                    if(read==0)break;count+=read;
                }
                var sample=Protocol.Parse(buffer.AsSpan(0,count),peer.Id);
                lock(sync)
                {
                    if(!Current(peer,port,version,out var e))return;
                    var now=clock.GetTimestamp();
                    if(e!.Snapshot==null || e.Snapshot.BootId!=sample.BootId || e.Snapshot.Sequence!=sample.Sequence)e.UnchangedSince=now;
                    e.Snapshot=sample;e.Received=now;e.LastAttempt=now;e.Attempted=true;e.Backoff=false;
                    e.Status=sample.AgeMs>10000||Age(e.UnchangedSince)>10000?"Stale":"Live";
                    if(e.Status=="Live")e.LastSuccess=clock.GetUtcNow();
                }
            }
            finally{slots.Release();}
        }
        catch(OperationCanceledException)when(networkToken.IsCancellationRequested||token.IsCancellationRequested){}
        catch(Exception ex)when(ex is HttpRequestException or IOException or OperationCanceledException or ProtocolException)
        {
            string status=ex is ProtocolException p?p.Message:"Agent unavailable";
            UpdateFailure(peer,port,version,status,true);
        }
    }
    bool Current(Peer peer,int port,int version,out Entry? e)
    {
        e=null;
        return generation==version && entries.TryGetValue(peer.Id,out e) && e.Monitored && e.Peer.Online &&
            e.Peer.Addresses.SequenceEqual(peer.Addresses) && settings.For(key).Ports.GetValueOrDefault(peer.Id,48173)==port;
    }
    void UpdateFailure(Peer peer,int port,int version,string status,bool backoff)
    {
        lock(sync)
        {
            if(!Current(peer,port,version,out var e))return;
            e!.Status=e.Snapshot!=null?"Stale":status;
            e.Backoff=backoff;e.Attempted=true;e.LastAttempt=clock.GetTimestamp();
        }
    }
    public void Dispose(){lock(sync){scope.Cancel();scope.Dispose();}}
}
