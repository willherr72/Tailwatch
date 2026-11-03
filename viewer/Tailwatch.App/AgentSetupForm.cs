using System.Diagnostics;
using Tailwatch.Core;
namespace Tailwatch.App;

internal sealed class AgentSetupForm : Form
{
    readonly TailwatchMonitor monitor;
    readonly string networkKey;
    readonly string? machineId;
    readonly RadioButton windows=new(){Text="Windows x64",AutoSize=true},linux=new(){Text="Linux x64",AutoSize=true},mac=new(){Text="Apple silicon Mac",AutoSize=true};
    readonly CheckBox updating=new(){Text="Update an existing agent",AutoSize=true};
    readonly Label guidance=new(){AutoSize=false,UseMnemonic=false};
    readonly TextBox command=new(){Multiline=true,ReadOnly=true,WordWrap=true,BorderStyle=BorderStyle.None,BackColor=Theme.Card,ForeColor=Theme.Text};
    readonly Button download,copy;
    readonly System.Windows.Forms.Timer timer=new(){Interval=500};
    readonly CancellationTokenSource lifetime=new();
    readonly Func<CancellationToken,Task<DirectorySnapshot>> discover;
    bool checking,invalidated;
    AgentInstructions? instructions;
    public AgentSetupForm(TailwatchMonitor monitor,string? machineId=null,Func<CancellationToken,Task<DirectorySnapshot>>? discover=null)
    {
        SuspendLayout();
        this.monitor=monitor;this.machineId=machineId;networkKey=monitor.NetworkKey;
        this.discover=discover??Tailscale.ReadAsync;
        Text="Tailwatch · Set up an agent";Name="AgentSetup";StartPosition=FormStartPosition.CenterParent;
        BackColor=Theme.Background;ForeColor=Theme.Text;Font=new Font("Segoe UI",9f);
        AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;ClientSize=new Size(620,540);
        FormBorderStyle=FormBorderStyle.FixedDialog;MinimizeBox=false;MaximizeBox=false;
        var target=monitor.Machines.FirstOrDefault(m=>m.Peer.Id==machineId);
        var title=new Label{Text=target==null?"Set up a machine to report its load":"Set up "+target.Peer.Name,UseMnemonic=false,Font=new Font("Segoe UI",13f,FontStyle.Bold),AutoEllipsis=true,Bounds=new Rectangle(20,16,580,34)};
        var intro=new Label{Text="1. Get the ZIP onto that machine and extract it.\n2. Open a terminal there and run the command below.",Bounds=new Rectangle(20,58,580,44),ForeColor=Theme.Muted};
        var platforms=new FlowLayoutPanel{Bounds=new Rectangle(20,111,580,30)};
        foreach(var radio in new[]{windows,linux,mac}){radio.Margin=new Padding(0,3,22,3);radio.CheckedChanged+=(_,_)=>Rebuild();platforms.Controls.Add(radio);}
        updating.SetBounds(20,149,400,25);updating.CheckedChanged+=(_,_)=>Rebuild();
        guidance.SetBounds(20,187,580,132);guidance.ForeColor=Theme.Muted;
        var box=new Panel{BackColor=Theme.Card,Padding=new Padding(12),Bounds=new Rectangle(20,329,580,142)};
        command.Dock=DockStyle.Fill;command.AccessibleName="Agent installation command";command.Font=new Font("Consolas",9f);box.Controls.Add(command);
        download=Theme.Button("Download ZIP",(_,_)=>Open(instructions?.Download));download.SetBounds(20,490,140,32);download.AutoSize=false;
        copy=Theme.Button("Copy command",async(_,_)=>await CopyCommandAsync());copy.SetBounds(170,490,150,32);copy.AutoSize=false;copy.AccessibleName="Copy agent installation command";
        var docs=Theme.Button("Setup guide",(_,_)=>Open(new Uri("https://github.com/willherr72/Tailwatch/blob/v0.1.0/README.md")));docs.SetBounds(330,490,130,32);docs.AutoSize=false;
        var close=Theme.Button("Close",(_,_)=>Close());close.SetBounds(480,490,120,32);close.AutoSize=false;CancelButton=close;
        Controls.AddRange([title,intro,platforms,updating,guidance,box,download,copy,docs,close]);
        var os=target?.Peer.Os.ToLowerInvariant();mac.Checked=os is "macos" or "darwin";linux.Checked=os=="linux";windows.Checked=!mac.Checked&&!linux.Checked;
        timer.Tick+=(_,_)=>{if(monitor.NetworkKey!=networkKey||monitor.Error!=null)Rebuild();};timer.Start();Rebuild();
        ResumeLayout(true);
    }
    void Rebuild()
    {
        if(copy==null)return; // Radio events can occur during construction.
        if(invalidated||networkKey.Length==0||monitor.NetworkKey!=networkKey||monitor.Error!=null)
        {
            instructions=null;command.Clear();command.Enabled=copy.Enabled=download.Enabled=false;
            guidance.Text="Connect to the intended tailnet, then reopen setup so the command uses the correct viewer addresses.";return;
        }
        var self=monitor.Machines.FirstOrDefault(m=>m.Peer.IsSelf);
        if(self==null){instructions=null;command.Clear();copy.Enabled=download.Enabled=false;guidance.Text="Waiting for this viewer's Tailscale identity. Reopen setup once connected.";return;}
        try
        {
            instructions=AgentSetup.Create(mac.Checked?AgentPlatform.MacArm64:linux.Checked?AgentPlatform.LinuxX64:AgentPlatform.WindowsX64,self.Peer.Addresses,machineId==null?48173:monitor.GetPort(machineId),updating.Checked);
            guidance.Text=instructions.Guidance;command.Text=instructions.Command;command.Enabled=true;copy.Enabled=!checking&&command.Text.Length>0;download.Enabled=true;download.Text=updating.Checked?"Choose release":"Download ZIP";copy.Text=checking?"Checking…":"Copy command";
        }
        catch(ArgumentException e){instructions=null;command.Clear();copy.Enabled=download.Enabled=false;guidance.Text=e.Message;}
    }
    async Task CopyCommandAsync()
    {
        if(checking)return;
        Rebuild();if(!copy.Enabled||instructions==null)return;
        checking=true;copy.Enabled=false;copy.Text="Checking…";
        try
        {
            // Background discovery runs less often while this dialog is open.
            // Validate the actual client now, not merely the cached profile key.
            var directory=await discover(lifetime.Token);
            if(IsDisposed)return;
            if(directory.Key!=networkKey||monitor.NetworkKey!=networkKey||monitor.Error!=null)
            {invalidated=true;Rebuild();return;}
            instructions=AgentSetup.Create(mac.Checked?AgentPlatform.MacArm64:linux.Checked?AgentPlatform.LinuxX64:AgentPlatform.WindowsX64,directory.Self.Addresses,machineId==null?48173:monitor.GetPort(machineId),updating.Checked);
            command.Text=instructions.Command;
            if(command.Text.Length==0){guidance.Text=instructions.Guidance;return;}
            Clipboard.SetText(instructions.Command);copy.Text="Copied";
        }
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
        catch(Exception e)when(e is IOException or ProtocolException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {if(!IsDisposed){invalidated=true;Rebuild();guidance.Text="Could not verify the active tailnet. Reconnect Tailscale, then reopen setup.";}}
        catch(System.Runtime.InteropServices.ExternalException)
        {if(!IsDisposed)guidance.Text="The clipboard is busy. Select and copy the verified command manually.";}
        finally{checking=false;if(!IsDisposed){copy.Enabled=!invalidated&&monitor.Error==null&&monitor.NetworkKey==networkKey&&command.Text.Length>0;if(copy.Text=="Checking…")copy.Text="Copy command";}}
    }
    void Open(Uri? uri)
    {
        if(uri==null)return;
        try{using var process=Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});}
        catch(System.ComponentModel.Win32Exception){MessageBox.Show(this,"Could not open your browser. Visit github.com/willherr72/Tailwatch/releases.","Tailwatch");}
    }
    protected override void Dispose(bool disposing){if(disposing){lifetime.Cancel();timer.Dispose();command.Font.Dispose();}base.Dispose(disposing);}
}
