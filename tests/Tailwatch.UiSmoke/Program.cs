using System.Net;
using Tailwatch.App;
using Tailwatch.Core;
internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        const string unicodeDirectory="""{"BackendState":"Running","CurrentTailnet":{"Name":"Test tailnet","MagicDNSSuffix":"test.ts.net"},"Self":{"ID":"test-self","HostName":"William’s Mac mini — café & lab","OS":"macOS","TailscaleIPs":["100.64.0.1"]}}""";
        if(args is ["status","--json"])
        {
            using var output=Console.OpenStandardOutput();output.Write(System.Text.Encoding.UTF8.GetBytes(unicodeDirectory));return;
        }
        if(args is ["switch","--list","--json"])
        {
            using var output=Console.OpenStandardOutput();output.Write(System.Text.Encoding.UTF8.GetBytes("""[{"id":"a123","nickname":"home","tailnet":"William’s home","account":"same@example.com","selected":true},{"id":"b456","nickname":"work","tailnet":"Work & lab","account":"same@example.com","selected":false}]"""));return;
        }
        if(args is ["switch","b456"])return;
        if(args is ["switch","eeee"]){Environment.ExitCode=1;return;}
        if(args is ["switch","ffff"]){Thread.Sleep(30000);return;}
        var executable=Environment.GetEnvironmentVariable("TAILWATCH_TAILSCALE");var encoding=Console.OutputEncoding;
        try
        {
            Environment.SetEnvironmentVariable("TAILWATCH_TAILSCALE",Environment.ProcessPath);
            Console.OutputEncoding=System.Text.Encoding.Latin1;
            var directory=Tailscale.ReadAsync().GetAwaiter().GetResult();
            if(directory.Self.Name!="William’s Mac mini — café & lab")throw new Exception("UTF-8 machine name was corrupted by the console code page");
            var accounts=Tailscale.ReadAccountsAsync().GetAwaiter().GetResult();
            if(accounts.Count!=2||accounts[0].Tailnet!="William’s home"||accounts[1].Id!="b456")throw new Exception("Saved account CLI discovery failed");
            Tailscale.SwitchAccountAsync("b456").GetAwaiter().GetResult();
            bool rejected=false;try{Tailscale.SwitchAccountAsync("eeee").GetAwaiter().GetResult();}catch(ProtocolException){rejected=true;}
            if(!rejected)throw new Exception("Failed account switch was reported as successful");
            using var cancel=new CancellationTokenSource(200);
            bool cancelled=false;try{Tailscale.SwitchAccountAsync("ffff",cancel.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
            if(!cancelled)throw new Exception("Account switch ignored cancellation");
        }
        finally{Console.OutputEncoding=encoding;Environment.SetEnvironmentVariable("TAILWATCH_TAILSCALE",executable);}
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        CheckAgentSetup(args.Length>0?Path.ChangeExtension(args[0],"setup.png"):null);
        using var panel=new TrayPanel();
        var rows=new List<MachineState>();
        foreach(var (id,name,os,cpu,gpu,ram) in new[]{("a","GPU workstation","linux",54d,92d,78d),("b","Mac Studio","macOS",26d,11d,61d),("c","Windows desktop","windows",8d,4d,42d)})
        {
            var p=new Peer(id,name,os,[IPAddress.Parse("100.64.0.1")],true);
            var s=new Snapshot{NodeId=id,BootId="demo",IntervalMs=2000,Cpu=new(){Value=cpu,Status="ok",Model="Example CPU",LogicalCores=12},Ram=new(8UL<<30,16UL<<30,"ok"),Gpus=[new("gpu","Example GPU","NVIDIA",new(gpu,"ok"),"active_time")]};
            var readings=Enumerable.Range(0,61).Select(i=>new HistoryPoint(TimeSpan.FromSeconds(900-i*15),i is >=27 and <=30?null:Math.Clamp(cpu+Math.Sin(i/4d)*18,0,100),i is >=27 and <=30?null:Math.Clamp(gpu+Math.Sin(i/6d)*12,0,100),i is >=27 and <=30?null:Math.Clamp(ram+Math.Cos(i/8d)*7,0,100))).ToArray();
            rows.Add(new(p,true,"Live",s,DateTimeOffset.Now,cpu,gpu,ram,History:readings));
        }
        rows.Add(new(new("d","Build server","linux",[IPAddress.Parse("100.64.0.4")],false),true,"Offline"));
        panel.UpdateMachines("Sample tailnet",rows,null);
        panel.StartPosition=FormStartPosition.Manual;panel.Location=args.Contains("--capture-screen")?new Point(40,40):new Point(-2000,-2000);panel.Show();Application.DoEvents();
        if(Find(panel).OfType<TextBox>().Count()!=1)throw new Exception("Search missing");
        var list=Find(panel).OfType<FlowLayoutPanel>().Single();
        if(list.Controls.OfType<MachineCard>().Count()!=4)throw new Exception("Machine rows missing");
        CheckReportingFilter(panel,rows);
        CheckAccountMenu(panel,rows);
        var card=list.Controls.OfType<MachineCard>().First();
        card.Controls.OfType<Button>().Single().PerformClick();Application.DoEvents();
        if(card.Height<=100)throw new Exception("Details did not expand");
        var history=Find(card).SingleOrDefault(c=>c.AccessibleName=="CPU, GPU and RAM history")??throw new Exception("Expanded history graphs missing");
        if(!history.Visible||history.Bottom>card.ClientSize.Height||history.Height<70)throw new Exception("Expanded history graph is hidden or clipped");
        if(args.Length>0)Capture(panel,Path.ChangeExtension(args[0],"history.png"),false);
        card.Controls.OfType<Button>().Single().PerformClick();
        var search=Find(panel).OfType<TextBox>().Single();
        search.Text="Mac";Application.DoEvents();
        if(list.Controls.OfType<MachineCard>().Count(c=>c.Visible)!=1)throw new Exception("Search filter failed");
        search.Text="";panel.SetSort("GPU");Application.DoEvents();
        if(((MachineCard)list.Controls[0]).MachineId!="a")throw new Exception("GPU sort failed");
        CheckStableRefresh(panel,rows);
        if(args.Length>0)Capture(panel,args[0],args.Contains("--capture-screen"));
        panel.UpdateMachines("Tailscale disconnected",[],"Connect Tailscale to see your machines.");Application.DoEvents();
        var launch=Find(panel).OfType<Button>().SingleOrDefault(b=>b.Text=="Open Tailscale" && b.Visible);
        if(launch==null)throw new Exception("Disconnected panel has no Open Tailscale action");
        int launches=0;panel.OpenTailscaleRequested+=()=>launches++;launch.PerformClick();
        if(launches!=1)throw new Exception("Open Tailscale click was not dispatched");
        if(search.Enabled)throw new Exception("Disconnected search should be disabled");
        if(Find(panel).OfType<Label>().Any(l=>l.Visible && l.Text.Contains("last known tailnet")))throw new Exception("Disconnected state claims a last known tailnet");
        var heading=Find(panel).OfType<Label>().Single(l=>l.Text=="Tailwatch");
        if(heading.Height<TextRenderer.MeasureText(heading.Text,heading.Font).Height)throw new Exception("Heading clips at display DPI");
        if(panel.Controls.OfType<ComboBox>().Any())throw new Exception("Unstyled native dropdown remains");
        if(args.Length>0)Capture(panel,Path.ChangeExtension(args[0],"offline.png"),args.Contains("--capture-screen"));
        panel.UpdateMachines("Sample tailnet",rows,null);Application.DoEvents();
        if(!search.Enabled || Find(panel).OfType<Button>().Any(b=>b.Text=="Open Tailscale" && b.Visible))throw new Exception("Connected controls did not recover");
        var sort=Find(panel).OfType<Button>().Single(b=>b.AccessibleName=="Sort machines");
        var sortMenu=sort.ContextMenuStrip??throw new Exception("Sort menu missing");
        sortMenu.Items.OfType<ToolStripMenuItem>().Single(i=>i.Text=="CPU").PerformClick();
        if(panel.SortValue!="CPU" || !sortMenu.Items.OfType<ToolStripMenuItem>().Single(i=>i.Text=="CPU").Checked)throw new Exception("Sort menu selection failed");
        foreach(var machine in list.Controls.OfType<MachineCard>())
        {
            var title=machine.Controls.OfType<Button>().Single();
            if(title.Height<title.GetPreferredSize(Size.Empty).Height)throw new Exception("Machine heading clips at display DPI");
        }
        if(args.Length>0)Capture(panel,Path.ChangeExtension(args[0],"connected.png"),false);
        var close=Find(panel).OfType<Button>().Single(b=>b.AccessibleName=="Close panel");
        close.PerformClick();if(panel.Visible)throw new Exception("Close button did not dismiss the panel");
        if(args.Length>0)
        {
            sortMenu.Show(new Point(-2000,-2000));Application.DoEvents();
            using var menuImage=new Bitmap(sortMenu.Width,sortMenu.Height);sortMenu.DrawToBitmap(menuImage,sortMenu.ClientRectangle);
            menuImage.Save(Path.ChangeExtension(args[0],"menu.png"));sortMenu.Close();
        }
        Console.WriteLine("Native UI smoke passed: setup/platform and stale-address protection, expanded history graphs, reporting filter and recovery, account menu and CLI success/failure/cancellation, UTF-8 process output, scrollbar drag/keys/wheel, steady 43-machine refresh, search, DPI, offline recovery, close action.");
    }
    static IEnumerable<Control> Find(Control parent)=>parent.Controls.Cast<Control>().SelectMany(c=>new[]{c}.Concat(Find(c)));
    static void CheckAgentSetup(string? capture)
    {
        using var client=new HttpClient();using var monitor=new TailwatchMonitor(client,new());
        var self=new Peer("viewer","Viewer","windows",[IPAddress.Parse("100.64.0.10")],true,true);
        var target=new Peer("mac","Test Mac","macOS",[IPAddress.Parse("100.64.0.11")],true);
        monitor.SetDirectory(new("home","Sample network",self,[self,target]));
        using var form=new AgentSetupForm(monitor,"mac");form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-2000,-2000);form.Show();Application.DoEvents();
        var command=Find(form).OfType<TextBox>().Single();var copy=Find(form).OfType<Button>().Single(b=>b.AccessibleName=="Copy agent installation command");
        if(!copy.Enabled||!command.Text.Contains("install-macos.sh")||!command.Text.Contains("100.64.0.10"))throw new Exception("Guided setup did not use selected platform and viewer IP");
        if(form.Controls.Cast<Control>().Any(c=>c.Visible&&(c.Bottom>form.ClientSize.Height||c.Right>form.ClientSize.Width)))throw new Exception("Setup controls extend outside the dialog at display DPI");
        if(capture!=null)Capture(form,capture,false);
        Find(form).OfType<CheckBox>().Single().Checked=true;
        if(command.Text.Contains("install-macos.sh")||!command.Text.Contains("launchctl bootstrap"))throw new Exception("Update mode did not preserve existing configuration");
        monitor.DiscoveryFailed("Temporary discovery error");Thread.Sleep(600);Application.DoEvents();
        if(copy.Enabled||command.Text.Length!=0)throw new Exception("Setup retained copyable addresses after discovery failed");
        monitor.SetDirectory(new("home","Sample network",self,[self,target]));
        monitor.SetDirectory(new("work","Other network",self with {Id="other"},[self with {Id="other"}]));copy.PerformClick();
        if(copy.Enabled||command.Text.Length!=0)throw new Exception("Setup copied stale viewer addresses after a network change");
        form.Close();
        foreach(bool failed in new[]{false,true})
        {
            monitor.SetDirectory(new("home","Sample network",self,[self,target]));int requests=0;
            using var verify=new AgentSetupForm(monitor,"mac",_=>{
                requests++;
                return failed?Task.FromException<DirectorySnapshot>(new IOException("discovery failed")):Task.FromResult(new DirectorySnapshot("external-switch","Other network",self,[self]));
            });
            verify.StartPosition=FormStartPosition.Manual;verify.Location=new Point(-2000,-2000);verify.Show();Application.DoEvents();
            var verifyCopy=Find(verify).OfType<Button>().Single(b=>b.AccessibleName=="Copy agent installation command");verifyCopy.PerformClick();Application.DoEvents();
            if(requests!=1||verifyCopy.Enabled||Find(verify).OfType<TextBox>().Single().Text.Length!=0)throw new Exception("Copy did not freshly validate the actual tailnet before exposing commands");
            verify.Close();
        }
    }
    static void CheckAccountMenu(TrayPanel panel,List<MachineState> rows)
    {
        var button=Find(panel).OfType<Button>().Single(b=>b.AccessibleName=="Switch tailnet");
        var menu=button.ContextMenuStrip??throw new Exception("Account menu missing");
        int loads=0;panel.AccountsRequested+=()=>loads++;
        button.PerformClick();Application.DoEvents();
        if(loads!=1||!menu.Visible)throw new Exception("Account button did not open and load the menu");
        var accounts=new[]{new TailscaleAccount("a123","home","William’s home","same@example.com",true),new TailscaleAccount("b456","work","Work & lab","same@example.com",false)};
        panel.SetAccounts(accounts);Application.DoEvents();
        var current=(ToolStripMenuItem)menu.Items[0];var other=(ToolStripMenuItem)menu.Items[1];
        if(!current.Checked||current.Enabled||!other.Enabled||other.Text?.Contains("Work && lab")!=true)throw new Exception("Account menu selection or literal label is wrong");
        string? selected=null;panel.AccountSwitchRequested+=account=>selected=account.Id;other.PerformClick();
        if(selected!="b456")throw new Exception("Account menu did not dispatch the saved profile ID");
        menu.Close();menu.Show(button,new Point(0,button.Height));Application.DoEvents();
        if(loads!=2||menu.Items.OfType<ToolStripMenuItem>().Any(i=>i.Checked))throw new Exception("Context-menu opening reused stale accounts instead of refreshing");
        panel.SetAccounts([accounts[0] with {Selected=false},accounts[1] with {Selected=true}]);
        if(!((ToolStripMenuItem)menu.Items[0]).Enabled||!((ToolStripMenuItem)menu.Items[1]).Checked)throw new Exception("Reopened account menu cannot switch back");
        menu.Close();panel.Show();panel.Activate();Application.DoEvents();
        panel.SetSwitching("Work & lab");panel.UpdateMachines("",[],"Switching tailnet…");Application.DoEvents();
        if(button.Enabled||Find(panel).OfType<MachineCard>().Any(c=>c.Visible))throw new Exception("Switching kept stale rows or allowed duplicate switching");
        panel.SetSwitching(null);panel.UpdateMachines("Tailscale disconnected",[],"Disconnected");Application.DoEvents();
        if(!button.Enabled)throw new Exception("Disconnected users cannot switch accounts");
        panel.SetAccounts(null,"Accounts unavailable");
        if(menu.Items.OfType<ToolStripMenuItem>().Count(i=>i.Enabled)!=1)throw new Exception("Account list failure lost the Tailscale management action");
        panel.UpdateMachines("Sample tailnet",rows,null);Application.DoEvents();
    }
    static void CheckReportingFilter(TrayPanel panel,List<MachineState> original)
    {
        var toggle=Find(panel).OfType<Button>().SingleOrDefault(b=>b.AccessibleName=="Reporting only")??throw new Exception("Reporting filter missing");
        var list=Find(panel).OfType<FlowLayoutPanel>().Single();
        var unavailable=new MachineState(new("unavailable","No agent","linux",[IPAddress.Parse("100.64.0.5")],true),true,"Agent unavailable");
        var fleet=original.Append(unavailable).ToList();panel.UpdateMachines("Sample tailnet",fleet,null);
        toggle.PerformClick();Application.DoEvents();
        if(list.Controls.OfType<MachineCard>().Count(c=>c.Visible)!=3)throw new Exception("Reporting filter kept offline or unavailable machines");
        panel.UpdateMachines("Sample tailnet",fleet.Select(m=>m.Peer.Id=="a"?m with {Status="Stale"}:m).ToList(),null);Application.DoEvents();
        if(list.Controls.OfType<MachineCard>().Count(c=>c.Visible)!=2)throw new Exception("Reporting filter kept stale readings");
        panel.UpdateMachines("Sample tailnet",fleet,null);Application.DoEvents();
        if(list.Controls.OfType<MachineCard>().Count(c=>c.Visible)!=3)throw new Exception("Recovered machine did not reappear");
        toggle.PerformClick();Application.DoEvents();
        if(list.Controls.OfType<MachineCard>().Count(c=>c.Visible)!=5)throw new Exception("Filter did not restore hidden machines");
        panel.UpdateMachines("Sample tailnet",original,null);
    }
    static void CheckStableRefresh(TrayPanel panel,List<MachineState> original)
    {
        var fleet=original.Concat(Enumerable.Range(1,39).Select(i=>original[0] with {Peer=original[0].Peer with {Id="extra-"+i,Name=$"Worker {i:00}"}})).ToList();
        var list=Find(panel).OfType<FlowLayoutPanel>().Single();
        var sort=panel.SortValue;panel.SetSort("Name");panel.UpdateMachines("Sample tailnet",fleet,null);Application.DoEvents();
        var scrollbar=Find(panel).SingleOrDefault(c=>c.AccessibleName=="Machine list scrollbar")??throw new Exception("Themed machine scrollbar missing");
        Input(scrollbar,"OnKeyDown",new KeyEventArgs(Keys.End));Application.DoEvents();
        if(list.AutoScrollPosition.Y>=0)throw new Exception("Scrollbar End did not scroll");
        Input(scrollbar,"OnKeyDown",new KeyEventArgs(Keys.Home));Application.DoEvents();
        if(list.AutoScrollPosition.Y!=0)throw new Exception("Scrollbar Home did not return to top");
        Input(scrollbar,"OnKeyDown",new KeyEventArgs(Keys.PageDown));Application.DoEvents();
        if(list.AutoScrollPosition.Y>=0)throw new Exception("Scrollbar PageDown did not scroll");
        Input(scrollbar,"OnKeyDown",new KeyEventArgs(Keys.Home));
        Input(scrollbar,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,scrollbar.Width/2,8,0));
        Input(scrollbar,"OnMouseMove",new MouseEventArgs(MouseButtons.Left,0,scrollbar.Width/2,scrollbar.Height/2,0));
        Input(scrollbar,"OnMouseUp",new MouseEventArgs(MouseButtons.Left,1,scrollbar.Width/2,scrollbar.Height/2,0));Application.DoEvents();
        if(list.AutoScrollPosition.Y>=0)throw new Exception("Scrollbar thumb drag did not scroll");
        Input(scrollbar,"OnKeyDown",new KeyEventArgs(Keys.Home));
        Input(list,"OnMouseWheel",new MouseEventArgs(MouseButtons.None,0,5,5,-120));Application.DoEvents();
        if(list.AutoScrollPosition.Y>=0)throw new Exception("Native mouse wheel scrolling was lost");
        var existing=list.Controls.OfType<MachineCard>().ToDictionary(c=>c.MachineId);
        var expanded=existing["a"];expanded.Controls.OfType<Button>().Single().PerformClick();Application.DoEvents();
        var expandedHeight=expanded.Height;
        list.AutoScrollPosition=new Point(0,120);var scroll=list.AutoScrollPosition;
        int hidden=0;
        EventHandler changed=(sender,_)=>{if(sender is MachineCard {Visible:false})hidden++;};
        foreach(var row in existing.Values)row.VisibleChanged+=changed;
        try
        {
            for(int tick=0;tick<4;tick++)
            {
                panel.UpdateMachines("Sample tailnet",fleet.Select(r=>r with {Cpu=25+tick}).ToList(),null);Application.DoEvents();
                if(hidden!=0)throw new Exception("Refresh temporarily hid existing machine rows");
                if(expanded.Height!=expandedHeight)throw new Exception("Refresh collapsed expanded details");
                if(list.AutoScrollPosition!=scroll)throw new Exception("Refresh moved the scroll position");
                if(list.Controls.OfType<MachineCard>().Any(c=>!ReferenceEquals(c,existing[c.MachineId])))throw new Exception("Refresh recreated rows");
            }
        }
        finally{foreach(var row in existing.Values)row.VisibleChanged-=changed;}
        panel.UpdateMachines("Sample tailnet",original,null);panel.SetSort(sort);expanded.Controls.OfType<Button>().Single().PerformClick();list.AutoScrollPosition=Point.Empty;
    }
    static void Input(Control control,string method,EventArgs args)=>control.GetType().GetMethod(method,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(control,[args]);
    static void Capture(Form panel,string path,bool screen)
    {
        panel.Refresh();Application.DoEvents();
        using var image=new Bitmap(panel.Width,panel.Height);
        if(screen){using var graphics=Graphics.FromImage(image);graphics.CopyFromScreen(panel.Location,Point.Empty,panel.Size);}
        else panel.DrawToBitmap(image,new Rectangle(Point.Empty,panel.Size));
        image.Save(path);
    }
}
