using System.Net;
using Tailwatch.App;
using Tailwatch.Core;
internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
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
        panel.StartPosition=FormStartPosition.Manual;panel.Location=new Point(-2000,-2000);panel.Show();Application.DoEvents();
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
        if(args.Length>0){using var image=new Bitmap(panel.Width,panel.Height);panel.DrawToBitmap(image,panel.ClientRectangle);image.Save(args[0]);}
        panel.Hide();
        Console.WriteLine("Native UI smoke passed: rows, expand/collapse, search, GPU sort, rendering.");
    }
}
