using System.Net;
using Tailwatch.Core;
using Xunit;
namespace Tailwatch.Tests;
public class AgentSetupTests
{
    static readonly IPAddress[] Addresses=[IPAddress.Parse("100.64.0.10"),IPAddress.Parse("fd7a:115c:a1e0::10")];
    [Theory] [InlineData(AgentPlatform.WindowsX64,"windows-x64.zip")] [InlineData(AgentPlatform.LinuxX64,"linux-x64-agent.zip")] [InlineData(AgentPlatform.MacArm64,"macos-arm64-agent.zip")]
    public void PublishedDownloadAndBothViewerAddressesAreUsed(AgentPlatform platform,string suffix)
    {
        var setup=AgentSetup.Create(platform,Addresses);
        Assert.Equal("github.com",setup.Download.Host);Assert.EndsWith(suffix,setup.Download.AbsolutePath);
        Assert.Contains("v0.1.0",setup.Download.AbsolutePath);Assert.Contains("100.64.0.10",setup.Command);Assert.Contains("fd7a:115c:a1e0::10",setup.Command);
    }
    [Theory] [InlineData("192.168.1.5")] [InlineData("127.0.0.1")] [InlineData("8.8.8.8")]
    public void NonTailnetAddressesNeverBecomeInstallCommands(string address)=>Assert.Throws<ArgumentException>(()=>AgentSetup.Create(AgentPlatform.LinuxX64,[IPAddress.Parse(address)]));
    [Fact] public void DisconnectedSetupCannotInventAViewerAddress()=>Assert.Throws<ArgumentException>(()=>AgentSetup.Create(AgentPlatform.MacArm64,[]));
    [Fact] public void UnknownPlatformsAreRejected()=>Assert.Throws<ArgumentOutOfRangeException>(()=>AgentSetup.Create((AgentPlatform)99,Addresses));
    [Fact] public void CustomPortsAreHonestAboutInstallerSupport()
    {
        Assert.Contains("-Port 49000",AgentSetup.Create(AgentPlatform.WindowsX64,Addresses,49000).Command);
        var linux=AgentSetup.Create(AgentPlatform.LinuxX64,Addresses,49000);Assert.Empty(linux.Command);Assert.Contains("49000",linux.Guidance);
    }
    [Fact] public void UpdateInstructionsPreserveExistingAccessPolicy()
    {
        foreach(var platform in Enum.GetValues<AgentPlatform>())
        {
            var setup=AgentSetup.Create(platform,Addresses,48173,true);
            Assert.Equal(AgentSetup.Releases,setup.Download);
            Assert.DoesNotContain("configure",setup.Command);Assert.DoesNotContain("install-",setup.Command);Assert.DoesNotContain("100.64.0.10",setup.Command);
            Assert.Contains("configuration",setup.Guidance,StringComparison.OrdinalIgnoreCase);
        }
    }
}
