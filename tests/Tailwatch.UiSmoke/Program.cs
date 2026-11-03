using System.Net;
using Tailwatch.App;
using Tailwatch.Core;
internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        using var panel=new TrayPanel();
        var rows=new List<MachineState>();
        foreach(var (id,name,os,cpu,gpu,ram) in new[]{("a","GPU workstation","linux",54d,92d,78d),("b","Mac Studio","macOS",26d,11d,61d),("c","Windows desktop","windows",8d,4d,42d)})
        {
            var p=new Peer(id,name,os,[IPAddress.Parse("100.64.0.1")],true);
            var s=new Snapshot{NodeId=id,BootId="demo",IntervalMs=2000,Cpu=new(){Value=cpu,Status="ok",Model="Example CPU",LogicalCores=12},Ram=new(8UL<<30,16UL<<30,"ok"),Gpus=[new("gpu","Example GPU","NVIDIA",new(gpu,"ok"),"active_time")]};
            rows.Add(new(p,true,"Live",s,DateTimeOffset.Now,cpu,gpu,ram));
        }
        rows.Add(new(new("d","Build server","linux",[IPAddress.Parse("100.64.0.4")],false),true,"Offline"));
        panel.UpdateMachines("Sample tailnet",rows,null);
        panel.StartPosition=FormStartPosition.Manual;panel.Location=args.Contains("--capture-screen")?new Point(40,40):new Point(-2000,-2000);panel.Show();Application.DoEvents();
        if(panel.Controls.OfType<TextBox>().Count()!=1)throw new Exception("Search missing");
        var list=panel.Controls.OfType<FlowLayoutPanel>().Single();
        if(list.Controls.OfType<MachineCard>().Count()!=4)throw new Exception("Machine rows missing");
        var card=list.Controls.OfType<MachineCard>().First();
        card.Controls.OfType<Button>().Single().PerformClick();Application.DoEvents();
        if(card.Height<=100)throw new Exception("Details did not expand");
        card.Controls.OfType<Button>().Single().PerformClick();
        var search=panel.Controls.OfType<TextBox>().Single();
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
        Console.WriteLine("Native UI smoke passed: steady 43-machine refresh, scroll/details, search, sort, DPI, offline recovery, close action.");
    }
    static IEnumerable<Control> Find(Control parent)=>parent.Controls.Cast<Control>().SelectMany(c=>new[]{c}.Concat(Find(c)));
    static void CheckStableRefresh(TrayPanel panel,List<MachineState> original)
    {
        var fleet=original.Concat(Enumerable.Range(1,39).Select(i=>original[0] with {Peer=original[0].Peer with {Id="extra-"+i,Name=$"Worker {i:00}"}})).ToList();
        var list=panel.Controls.OfType<FlowLayoutPanel>().Single();
        var sort=panel.SortValue;panel.SetSort("Name");panel.UpdateMachines("Sample tailnet",fleet,null);Application.DoEvents();
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
    static void Capture(Form panel,string path,bool screen)
    {
        panel.Refresh();Application.DoEvents();
        using var image=new Bitmap(panel.Width,panel.Height);
        if(screen){using var graphics=Graphics.FromImage(image);graphics.CopyFromScreen(panel.Location,Point.Empty,panel.Size);}
        else panel.DrawToBitmap(image,panel.ClientRectangle);
        image.Save(path);
    }
}
