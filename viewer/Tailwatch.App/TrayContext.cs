using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Win32;
using Tailwatch.Core;
namespace Tailwatch.App;
internal sealed class TrayContext : ApplicationContext
{
    readonly NotifyIcon icon;
    readonly Icon drawing;
    readonly TrayPanel panel=new();
    readonly System.Windows.Forms.Timer timer=new(){Interval=500};
    readonly EventWaitHandle show;
    readonly string settingsPath=Path.Combine(Log.Folder,"settings.json");
    readonly AppSettings settings;
    readonly TailwatchMonitor monitor;
    readonly HttpClient http=new(new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false,ConnectTimeout=TimeSpan.FromSeconds(2),MaxConnectionsPerServer=2}){Timeout=Timeout.InfiniteTimeSpan};
    readonly CancellationTokenSource lifetime=new();
    Process? agent;
    bool busy,ending,switching;
    int directoryGeneration,accountRequest;
    DateTime next=DateTime.MinValue;
    public TrayContext(EventWaitHandle show)
    {
        this.show=show;settings=AppSettings.Load(settingsPath);monitor=new(http,settings);drawing=Theme.Icon();
        var menu=Theme.Menu();
        menu.Items.Add("Open",null,(_,_)=>Toggle(true));menu.Items.Add("Machines…",null,(_,_)=>OpenSettings());
        menu.Items.Add("Set up an agent…",null,(_,_)=>OpenSetup(null));
        menu.Items.Add("Downloads and updates…",null,(_,_)=>OpenDownloads());
        var startup=new ToolStripMenuItem("Start with Windows"){Checked=StartupEnabled(),CheckOnClick=true};
        startup.Click+=(_,_)=>{try{SetStartup(startup.Checked);}catch(Exception e){Log.Write(e.Message);startup.Checked=StartupEnabled();}};
        menu.Items.Add(startup);menu.Items.Add(new ToolStripSeparator());menu.Items.Add("Quit",null,(_,_)=>ExitThread());
        icon=new NotifyIcon{Icon=drawing,Text="Tailwatch · connecting",Visible=true,ContextMenuStrip=menu};
        icon.MouseClick+=(_,e)=>{if(e.Button==MouseButtons.Left)Toggle(!panel.Visible);};
        panel.RefreshRequested+=()=>{next=DateTime.MinValue;_=RefreshAsync(true);};
        panel.SettingsRequested+=OpenSettings;
        panel.OpenTailscaleRequested+=OpenTailscale;
        panel.SortChanged+=()=>{if(monitor.NetworkKey.Length>0){settings.For(monitor.NetworkKey).Sort=panel.SortValue;Save();}};
        panel.FilterChanged+=()=>{if(monitor.NetworkKey.Length>0){settings.For(monitor.NetworkKey).ReportingOnly=panel.ReportingOnly;Save();}};
        panel.AccountsRequested+=()=>_=LoadAccountsAsync();
        panel.AccountSwitchRequested+=account=>_=SwitchAccountAsync(account);
        timer.Tick+=async(_,_)=>{
            if(ending)return;
            if(show.WaitOne(0))Toggle(true);
            if(panel.Visible)UpdatePanel();
            if(DateTime.UtcNow>=next)await RefreshAsync(false);
        };
        timer.Start();_=StartLocalAgentAsync();_=RefreshAsync(true);
    }
    void Save(){try{settings.Save(settingsPath);}catch(Exception e)when(e is IOException or UnauthorizedAccessException){Log.Write("Could not save settings: "+e.Message);}}
    void Toggle(bool open)
    {
        if(!open){panel.Hide();return;}
        UpdatePanel();panel.ShowNearTray();next=DateTime.MinValue;_=RefreshAsync(true);
    }
    void OpenSettings()
    {
        if(switching)return;
        panel.Hide();using var dialog=new SettingsForm(monitor,Save,OpenSetup);dialog.ShowDialog();
        next=DateTime.MinValue;_=RefreshAsync(true);
    }
    void OpenSetup(string? machineId)
    {
        if(switching)return;
        panel.Hide();using var dialog=new AgentSetupForm(monitor,machineId);dialog.ShowDialog();
    }
    void OpenDownloads()
    {
        try{using var process=Process.Start(new ProcessStartInfo(AgentSetup.Releases.AbsoluteUri){UseShellExecute=true});}
        catch(System.ComponentModel.Win32Exception){MessageBox.Show("Could not open your browser. Visit github.com/willherr72/Tailwatch/releases.","Tailwatch");}
    }
    void OpenTailscale()
    {
        var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Tailscale","tailscale-ipn.exe");
        try
        {
            if(!File.Exists(path))throw new FileNotFoundException("Install Tailscale for Windows to connect your machines.");
            using var process=Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
            panel.Hide();next=DateTime.MinValue;
        }
        catch(Exception e)when(e is IOException or System.ComponentModel.Win32Exception)
        {MessageBox.Show(panel,e.Message,"Could not open Tailscale",MessageBoxButtons.OK,MessageBoxIcon.Information);}
    }
    void UpdatePanel()
    {
        if(ending)return;
        panel.UpdateMachines(monitor.NetworkName,monitor.Machines,monitor.Error);
        var live=monitor.Machines.Count(m=>m.Status=="Live");
        icon.Text=$"Tailwatch · {live} reporting";
    }
    async Task LoadAccountsAsync()
    {
        int request=++accountRequest;
        try
        {
            var accounts=await Tailscale.ReadAccountsAsync(lifetime.Token);
            if(!ending&&request==accountRequest)panel.SetAccounts(accounts);
        }
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
        catch(Exception e)when(e is IOException or ProtocolException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            if(!ending&&request==accountRequest)panel.SetAccounts(null,"Accounts unavailable. Open Tailscale below.");
        }
    }
    async Task SwitchAccountAsync(TailscaleAccount account)
    {
        if(switching||ending||account.Selected)return;
        switching=true;directoryGeneration++;accountRequest++;
        monitor.Disconnect("Switching tailnet…");panel.SetSwitching(account.Tailnet.Length>0?account.Tailnet:account.Nickname);UpdatePanel();
        string? failure=null;
        try{await Tailscale.SwitchAccountAsync(account.Id,lifetime.Token);}
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
        catch(Exception e)when(e is IOException or ProtocolException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            failure=e is ProtocolException?e.Message:e is OperationCanceledException?"The account switch timed out. Check the active account in Tailscale.":"Could not switch accounts. Check that Tailscale is running.";
        }
        finally
        {
            switching=false;
            if(!ending)
            {
                panel.SetSwitching(null);next=DateTime.MinValue;
                // Re-read the actual connection even on failure: the daemon can
                // complete a switch after the CLI has timed out.
                await RefreshAsync(true);
            }
        }
        if(failure!=null&&!ending)MessageBox.Show(panel,failure,"Could not switch tailnet",MessageBoxButtons.OK,MessageBoxIcon.Information);
    }
    async Task RefreshAsync(bool force)
    {
        if(busy||ending||switching)return;
        int generation=directoryGeneration;
        busy=true;
        try
        {
            var directory=await Tailscale.ReadAsync(lifetime.Token);
            if(ending||generation!=directoryGeneration)return;
            bool changed=directory.Key!=monitor.NetworkKey;
            monitor.SetDirectory(directory);
            if(changed){var profile=settings.For(directory.Key);panel.SetSort(profile.Sort);panel.SetReportingOnly(profile.ReportingOnly);}
            UpdatePanel();
            // Discovery must keep running even while remote peers are timing out.
            _=PollAndRenderAsync(force);
        }
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
        catch(Exception e)when(e is IOException or ProtocolException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            if(ending||generation!=directoryGeneration)return;
            var message=e is ProtocolException?e.Message:"Tailscale is unavailable. Check that it is running.";
            if(e is ProtocolException {Disconnected:true})monitor.Disconnect(message);
            else monitor.DiscoveryFailed(message);
            UpdatePanel();
        }
        finally{busy=false;next=generation!=directoryGeneration?DateTime.MinValue:DateTime.UtcNow.AddSeconds(panel.Visible?2:15);}
    }
    async Task PollAndRenderAsync(bool force)
    {
        try{await monitor.PollAsync(force,lifetime.Token);UpdatePanel();}
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
    }
    async Task StartLocalAgentAsync()
    {
        var path=Path.Combine(AppContext.BaseDirectory,"tailwatch-agent.exe");
        if(!File.Exists(path)){Log.Write("Local agent executable not found beside Tailwatch.exe.");return;}
        try
        {
            using var check=new TcpClient();
            try{await check.ConnectAsync(IPAddress.Loopback,48173,lifetime.Token);return;}
            catch(SocketException){}
            if(ending)return;
            var start=new ProcessStartInfo(path){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardError=true};
            start.ArgumentList.Add("run");start.ArgumentList.Add("--local-only");
            agent=Process.Start(start);
            if(agent!=null)
            {
                agent.ErrorDataReceived+=(_,e)=>{if(e.Data!=null)Log.Write("Local agent: "+e.Data);};
                agent.BeginErrorReadLine();
            }
        }
        catch(Exception e)when(e is IOException or System.ComponentModel.Win32Exception or OperationCanceledException){Log.Write("Local agent: "+e.Message);}
    }
    static bool StartupEnabled()
    {
        using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("Tailwatch")!=null;
    }
    static void SetStartup(bool enabled)
    {
        using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if(enabled)key.SetValue("Tailwatch","\""+Environment.ProcessPath+"\"");
        else key.DeleteValue("Tailwatch",false);
    }
    protected override void ExitThreadCore()
    {
        ending=true;timer.Stop();lifetime.Cancel();icon.Visible=false;Save();
        if(agent!=null){try{if(!agent.HasExited)agent.Kill(true);}catch(InvalidOperationException){}}
        base.ExitThreadCore();
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing)
        {
            ending=true;lifetime.Cancel();timer.Dispose();monitor.Dispose();http.Dispose();
            icon.Visible=false;icon.ContextMenuStrip?.Dispose();icon.Dispose();drawing.Dispose();panel.Dispose();agent?.Dispose();
        }
        base.Dispose(disposing);
    }
}
