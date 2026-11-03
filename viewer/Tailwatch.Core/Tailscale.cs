using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
namespace Tailwatch.Core;
public record Peer(string Id,string Name,string Os,List<IPAddress> Addresses,bool Online,bool IsSelf=false);
public record DirectorySnapshot(string Key,string Name,Peer Self,List<Peer> Peers);
public record TailscaleAccount(string Id,string Nickname,string Tailnet,string Account,bool Selected)
{
    public string Label => (Tailnet.Length>0?Tailnet:Nickname.Length>0?Nickname:Id)+(Account.Length>0?"  ·  "+Account:"");
}
public static class Tailscale
{
    public static bool IsTailnet(IPAddress address)
    {
        if(address.IsIPv4MappedToIPv6) address=address.MapToIPv4();
        var bytes=address.GetAddressBytes();
        return address.AddressFamily==AddressFamily.InterNetwork ? bytes[0]==100 && bytes[1] is >=64 and <128 :
            bytes[0]==0xfd && bytes[1]==0x7a && bytes[2]==0x11 && bytes[3]==0x5c && bytes[4]==0xa1 && bytes[5]==0xe0;
    }
    static string Text(JsonElement e,string name) => e.TryGetProperty(name,out var p) && p.ValueKind==JsonValueKind.String ? p.GetString()??"" : "";
    static Peer ParsePeer(JsonElement e,bool self=false)
    {
        var id=Text(e,"ID");
        var name=Text(e,"HostName");
        if(name.Length==0) name=Text(e,"DNSName").TrimEnd('.');
        var addresses=e.TryGetProperty("TailscaleIPs",out var ips) && ips.ValueKind==JsonValueKind.Array
            ? ips.EnumerateArray().Where(p=>p.ValueKind==JsonValueKind.String).Select(p=>IPAddress.TryParse(p.GetString(),out var ip)?ip:null)
                .Where(ip=>ip!=null && IsTailnet(ip)).Cast<IPAddress>().Distinct().ToList() : [];
        var online=self || (e.TryGetProperty("Online",out var o) && o.ValueKind==JsonValueKind.True);
        return new(id,name.Length>0?name:id,Text(e,"OS"),addresses,online,self);
    }
    public static DirectorySnapshot Parse(string json)
    {
        try
        {
            using var doc=JsonDocument.Parse(json);
            var r=doc.RootElement;
            var state=Text(r,"BackendState");
            if(state!="Running") throw new ProtocolException("Connect Tailscale to see your machines.",state is "Stopped" or "NeedsLogin" or "NeedsMachineAuth" or "NoState" or "Starting");
            var self=ParsePeer(r.GetProperty("Self"),true);
            if(self.Id.Length==0 || self.Addresses.Count==0) throw new ProtocolException("Tailscale has no usable local identity.");
            var tailnet=r.GetProperty("CurrentTailnet");
            var name=Text(tailnet,"Name");
            var suffix=Text(tailnet,"MagicDNSSuffix");
            if(string.IsNullOrEmpty(name) && string.IsNullOrEmpty(suffix)) throw new ProtocolException("Tailscale network identity is missing.");
            var peers=new List<Peer>{self};
            if(r.TryGetProperty("Peer",out var all) && all.ValueKind==JsonValueKind.Object)
                peers.AddRange(all.EnumerateObject().Select(p=>ParsePeer(p.Value)).Where(p=>p.Id.Length>0 && p.Addresses.Count>0 && p.Id!=self.Id));
            return new(suffix+"|"+self.Id,name.Length>0?name:suffix,self,peers.DistinctBy(p=>p.Id).ToList());
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new ProtocolException("Invalid Tailscale directory."); }
    }
    public static string Executable => Environment.GetEnvironmentVariable("TAILWATCH_TAILSCALE") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Tailscale","tailscale.exe");
    public static async Task<DirectorySnapshot> ReadAsync(CancellationToken token=default)
        =>Parse(await RunAsync(["status","--json"],TimeSpan.FromSeconds(2),"Tailscale is unavailable.",token));
    static bool ValidAccountId(string id)=>id.Length is >0 and <=64&&id.All(char.IsAsciiHexDigit);
    public static IReadOnlyList<TailscaleAccount> ParseAccounts(string json)
    {
        try
        {
            using var doc=JsonDocument.Parse(json);
            if(doc.RootElement.ValueKind!=JsonValueKind.Array)throw new ProtocolException("Invalid Tailscale account list.");
            var accounts=new List<TailscaleAccount>();var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var item in doc.RootElement.EnumerateArray())
            {
                var id=Text(item,"id");
                if(!ValidAccountId(id)||!ids.Add(id)||!item.TryGetProperty("selected",out var selected)||selected.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ProtocolException("Invalid Tailscale account list.");
                accounts.Add(new(id,Text(item,"nickname"),Text(item,"tailnet"),Text(item,"account"),selected.GetBoolean()));
            }
            return accounts;
        }
        catch(Exception e)when(e is JsonException or InvalidOperationException)
        {throw new ProtocolException("Invalid Tailscale account list.");}
    }
    public static async Task<IReadOnlyList<TailscaleAccount>> ReadAccountsAsync(CancellationToken token=default)
        =>ParseAccounts(await RunAsync(["switch","--list","--json"],TimeSpan.FromSeconds(3),"Could not list accounts. Open Tailscale to manage them.",token));
    public static async Task SwitchAccountAsync(string id,CancellationToken token=default)
    {
        if(!ValidAccountId(id))throw new ArgumentException("Invalid Tailscale account ID.",nameof(id));
        await RunAsync(["switch",id],TimeSpan.FromSeconds(15),"Tailscale could not switch accounts. Open Tailscale to check sign-in and permissions.",token);
    }
    static async Task<string> RunAsync(string[] arguments,TimeSpan duration,string failure,CancellationToken token)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(duration);
        var info=new ProcessStartInfo(Executable) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardOutputEncoding=System.Text.Encoding.UTF8,StandardErrorEncoding=System.Text.Encoding.UTF8 };
        foreach(var argument in arguments)info.ArgumentList.Add(argument);
        using var process=Process.Start(info)??throw new IOException("Could not start Tailscale.");
        try
        {
            var stdout=ReadLimitedAsync(process.StandardOutput,4*1024*1024,timeout.Token);
            var stderr=ReadLimitedAsync(process.StandardError,65536,timeout.Token);
            await Task.WhenAll(stdout,stderr,process.WaitForExitAsync(timeout.Token));
            if(process.ExitCode!=0) throw new ProtocolException(failure);
            return await stdout;
        }
        finally { if(!process.HasExited) process.Kill(true); }
    }
    static async Task<string> ReadLimitedAsync(StreamReader reader,int limit,CancellationToken token)
    {
        var text=new System.Text.StringBuilder();var buffer=new char[4096];
        int count;
        while((count=await reader.ReadAsync(buffer.AsMemory(),token))>0)
        {
            if(text.Length+count>limit) throw new ProtocolException("Tailscale response is too large.");
            text.Append(buffer,0,count);
        }
        return text.ToString();
    }
}

