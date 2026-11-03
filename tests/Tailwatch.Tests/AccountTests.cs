using Tailwatch.Core;
using Xunit;
namespace Tailwatch.Tests;
public class AccountTests
{
    [Fact] public void SavedAccountsKeepDistinctIdsAndUnicodeLabels()
    {
        var accounts=Tailscale.ParseAccounts("""[{"id":"a123","nickname":"home","tailnet":"William’s home","account":"same@example.com","selected":true},{"id":"b456","nickname":"work","tailnet":"Work & lab","account":"same@example.com","selected":false}]""");
        Assert.Equal(2,accounts.Count);
        Assert.Equal("a123",accounts[0].Id);Assert.True(accounts[0].Selected);
        Assert.Equal("b456",accounts[1].Id);Assert.False(accounts[1].Selected);
        Assert.Contains("William’s home",accounts[0].Label);Assert.Contains("Work & lab",accounts[1].Label);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("[{\"id\":\"--list\",\"selected\":false}]")]
    [InlineData("[{\"id\":\"remove\",\"selected\":false}]")]
    [InlineData("[{\"id\":\"a123\",\"selected\":\"true\"}]")]
    [InlineData("[{\"id\":\"a123\",\"selected\":true},{\"id\":\"a123\",\"selected\":false}]")]
    public void MalformedAccountListsAreRejected(string json)=>Assert.Throws<ProtocolException>(()=>Tailscale.ParseAccounts(json));
    [Theory] [InlineData("--list")] [InlineData("remove")] [InlineData("")] [InlineData("a123 b456")]
    public async Task InvalidAccountCannotExecuteCli(string id)=>await Assert.ThrowsAsync<ArgumentException>(()=>Tailscale.SwitchAccountAsync(id));
    [Fact] public void ReportingPreferencePersistsSeparatelyPerTailnet()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json");
        try
        {
            var settings=new AppSettings();settings.For("home").ReportingOnly=true;settings.For("work").ReportingOnly=false;settings.Save(path);
            var loaded=AppSettings.Load(path);Assert.True(loaded.For("home").ReportingOnly);Assert.False(loaded.For("work").ReportingOnly);
        }
        finally{File.Delete(path);}
    }
}
