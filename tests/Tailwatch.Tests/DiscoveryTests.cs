using System.Net;
using Tailwatch.Core;
using Xunit;
namespace Tailwatch.Tests;
public class DiscoveryTests
{
    const string Fixture = """{"BackendState":"Running","CurrentTailnet":{"Name":"personal.example","MagicDNSSuffix":"tail123.ts.net"},"Self":{"ID":"self-a","HostName":"laptop","OS":"windows","TailscaleIPs":["100.64.0.1"]},"Peer":{"key":{"ID":"node-b","HostName":"server","DNSName":"server.tail123.ts.net.","OS":"linux","TailscaleIPs":["100.64.0.2","192.168.0.2"],"Online":true}}}""";
    [Fact] public void DirectoryIncludesSelfAndOnlyTailnetAddresses()
    {
        var directory=Tailscale.Parse(Fixture);
        Assert.Equal("personal.example",directory.Name);
        Assert.Equal("self-a",directory.Self.Id);
        Assert.Equal(2,directory.Peers.Count);
        Assert.Equal("100.64.0.2",Assert.Single(directory.Peers.Single(p=>!p.IsSelf).Addresses).ToString());
    }
    [Fact] public void AccountSwitchChangesProfileKey() => Assert.NotEqual(Tailscale.Parse(Fixture).Key,Tailscale.Parse(Fixture.Replace("self-a","self-other")).Key);
    [Fact] public void DisconnectedDirectoryIsNotAccepted() => Assert.Throws<ProtocolException>(()=>Tailscale.Parse(Fixture.Replace("Running","Stopped")));
    [Theory] [InlineData("0.0.0.0")] [InlineData("192.168.0.1")] [InlineData("8.8.8.8")]
    public void UnsafeAddressesAreRejected(string address)=>Assert.False(Tailscale.IsTailnet(IPAddress.Parse(address)));
    [Fact] public void MalformedDirectoryIsRejected()=>Assert.Throws<ProtocolException>(()=>Tailscale.Parse("{}"));
}

