using System.Net;
namespace Tailwatch.Core;

public enum AgentPlatform { WindowsX64,LinuxX64,MacArm64 }
public record AgentInstructions(Uri Download,string Command,string Guidance);
public static class AgentSetup
{
    // Agents use the same v1 protocol. Keep setup on an actually published release.
    public const string AgentRelease="0.1.0";
    public static readonly Uri Releases=new("https://github.com/willherr72/Tailwatch/releases");
    public static AgentInstructions Create(AgentPlatform platform,IEnumerable<IPAddress> viewerAddresses,int port=48173,bool update=false)
    {
        if(!Enum.IsDefined(platform))throw new ArgumentOutOfRangeException(nameof(platform));
        if(port is <1 or >65535)throw new ArgumentOutOfRangeException(nameof(port));
        var addresses=viewerAddresses.Select(a=>a.IsIPv4MappedToIPv6?a.MapToIPv4():a).Distinct().ToArray();
        if(addresses.Length==0||addresses.Any(a=>!Tailscale.IsTailnet(a)))throw new ArgumentException("Connect Tailscale to get this viewer’s addresses.",nameof(viewerAddresses));
        var suffix=platform switch{AgentPlatform.WindowsX64=>"windows-x64",AgentPlatform.LinuxX64=>"linux-x64-agent",_=>"macos-arm64-agent"};
        var download=new Uri($"https://github.com/willherr72/Tailwatch/releases/download/v{AgentRelease}/Tailwatch-v{AgentRelease}-{suffix}.zip");
        string guidance=platform switch {
            AgentPlatform.WindowsX64=>"Windows x64: open PowerShell as Administrator in the extracted folder. Quit the tray app on that machine before installing a service. If Windows blocks the downloaded script, use Unblock-File .\\install-windows.ps1 after checking the download.",
            AgentPlatform.LinuxX64=>"Linux x64: open a terminal in the extracted folder. The packaged installer needs sudo and systemd; the service starts at boot.",
            _=>"Apple silicon only: open Terminal in the extracted folder. Run as your normal user, without sudo. The agent starts at sign-in and runs while that user session is active."
        };
        if(update)return new(Releases,UpdateCommand(platform),guidance+"\nChoose the desired version's matching ZIP from Releases. Update an agent installed with the packaged installer. Existing configuration and allowed viewer addresses are preserved; this does not add a new viewer.");
        if(port!=48173&&platform!=AgentPlatform.WindowsX64)
            return new(download,"",guidance+$"\nThis machine is configured for port {port}. The packaged installer uses 48173. Set the machine's port to 48173 before installing, or use manual configuration from the README.");
        var command=platform==AgentPlatform.WindowsX64
            ?$"& .\\install-windows.ps1 -AllowPeer @({string.Join(", ",addresses.Select(a=>"'"+a+"'"))}) -Port {port}"
            :$"{(platform==AgentPlatform.LinuxX64?"sudo ":"")}bash ./install-{(platform==AgentPlatform.LinuxX64?"linux":"macos")}.sh ./tailwatch-agent {string.Join(" ",addresses.Select(a=>a.ToString()))}";
        return new(download,command,guidance+"\nConnect that machine to the same tailnet first. After installing, return here and click Refresh. These addresses authorize this viewer only.");
    }
    static string UpdateCommand(AgentPlatform platform)=>platform switch {
        AgentPlatform.WindowsX64=>"""
            $ErrorActionPreference = 'Stop'
            Stop-Service TailwatchAgent
            try { Copy-Item -LiteralPath .\tailwatch-agent.exe -Destination "$env:ProgramFiles\Tailwatch\tailwatch-agent.exe" -Force }
            finally { Start-Service TailwatchAgent }
            """,
        AgentPlatform.LinuxX64=>"""
            sudo install -m 755 ./tailwatch-agent /usr/local/lib/tailwatch/tailwatch-agent.new &&
            sudo systemctl stop tailwatch-agent &&
            sudo mv /usr/local/lib/tailwatch/tailwatch-agent.new /usr/local/lib/tailwatch/tailwatch-agent &&
            sudo systemctl start tailwatch-agent
            """,
        _=>"""
            dir="$HOME/Library/Application Support/Tailwatch"
            install -m 755 ./tailwatch-agent "$dir/tailwatch-agent.new" &&
            launchctl bootout "gui/$(id -u)/com.tailwatch.agent" &&
            mv "$dir/tailwatch-agent.new" "$dir/tailwatch-agent" &&
            launchctl bootstrap "gui/$(id -u)" "$HOME/Library/LaunchAgents/com.tailwatch.agent.plist"
            """
    };
}
