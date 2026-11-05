using Tailwatch.Core;
namespace Tailwatch.App;
internal sealed class Meter : Control
{
    int S(int value)=>(int)Math.Round(value*DeviceDpi/96f);
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public double? Value {get;set;}
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Caption {get;set;}="";
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Partial {get;set;}
    public Meter(){DoubleBuffered=true;Height=40;TabStop=false;}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var number=Value is double v?$"{v:0}%{(Partial?"*":"")}":"—";
        TextRenderer.DrawText(e.Graphics,Caption,Font,new Rectangle(0,0,Width/2,S(20)),Theme.Muted,TextFormatFlags.Left);
        TextRenderer.DrawText(e.Graphics,number,Font,new Rectangle(Width/2,0,Width/2,S(20)),Value>=90?Theme.Hot:Theme.Text,TextFormatFlags.Right);
        using var track=new SolidBrush(Theme.Border);using var fill=new SolidBrush(Theme.Load(Value));
        e.Graphics.FillRectangle(track,0,S(27),Width,S(4));
        if(Value.HasValue)e.Graphics.FillRectangle(fill,0,S(27),(float)(Width*Math.Clamp(Value.Value,0,100)/100),S(4));
        AccessibleName=$"{Caption}: {(Value is null?"unavailable":number)}";
    }
}
internal sealed class MachineCard : UserControl
{
    readonly Button title;
    readonly Label status;
    readonly Meter cpu=new(){Caption="CPU"},gpu=new(){Caption="GPU"},ram=new(){Caption="RAM"};
    readonly Label detail;
    readonly HistoryGraph history=new(){Visible=false};
    readonly ToolTip tooltip=new();
    readonly TableLayoutPanel meters;
    MachineState state;
    bool expanded;
    public string MachineId=>state.Peer.Id;
    int S(int value)=>(int)Math.Round(value*DeviceDpi/96f);
    public MachineCard(MachineState state)
    {
        this.state=state;DoubleBuffered=true;BackColor=Theme.Background;ForeColor=Theme.Text;
        Margin=new Padding(0);Padding=new Padding(0,9,0,8);Height=88;
        title=new Button{FlatStyle=FlatStyle.Flat,TextAlign=ContentAlignment.MiddleLeft,ForeColor=Theme.Text,BackColor=Theme.Background,
            Font=new Font("Segoe UI",9.5f,FontStyle.Bold),Location=new Point(0,6),Height=27,Width=290,Cursor=Cursors.Hand,AutoEllipsis=true,UseMnemonic=false};
        title.FlatAppearance.BorderSize=0;title.Click+=(_,_)=>{expanded=!expanded;UpdateDetails();};
        status=new Label{TextAlign=ContentAlignment.MiddleRight,ForeColor=Theme.Muted,Location=new Point(290,12),Size=new Size(160,18),AutoEllipsis=true};
        meters=new TableLayoutPanel{ColumnCount=3,RowCount=1,Location=new Point(4,43),Height=40,Anchor=AnchorStyles.Left|AnchorStyles.Top|AnchorStyles.Right};
        for(int i=0;i<3;i++)meters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,33.333f));
        foreach(var meter in new[]{cpu,gpu,ram}){meter.Dock=DockStyle.Fill;meter.Margin=new Padding(0,0,16,0);meters.Controls.Add(meter);}
        detail=new Label{ForeColor=Theme.Muted,BackColor=Theme.Card,Padding=new Padding(10),Location=new Point(4,91),AutoSize=false,Visible=false,UseMnemonic=false};
        Controls.AddRange([title,status,meters,detail,history]);
        Resize+=(_,_)=>LayoutChildren();
        UpdateState(state);
    }
    void LayoutChildren()
    {
        title.SetBounds(0,S(4),Math.Max(S(90),Width-S(150)),Math.Max(S(32),title.GetPreferredSize(Size.Empty).Height));
        status.SetBounds(Width-S(150),S(12),S(140),S(20));
        meters.SetBounds(S(4),S(43),Width-S(8),S(40));
        foreach(var meter in new[]{cpu,gpu,ram})meter.Margin=new Padding(0,0,S(16),0);
        detail.SetBounds(S(4),S(91),Width-S(8),detail.Height);detail.Padding=new Padding(S(10));
        history.SetBounds(S(4),detail.Bottom+S(6),Width-S(8),S(124));
    }
    protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);LayoutChildren();UpdateDetails();}
    protected override void OnDpiChangedAfterParent(EventArgs e){base.OnDpiChangedAfterParent(e);LayoutChildren();UpdateDetails();}
    public void UpdateState(MachineState value)
    {
        state=value;
        title.AccessibleName=$"{value.Peer.Name}. Expand machine details";
        status.Text=value.Status=="Live"?value.Peer.Os:value.Status;
        var description=new List<string>{value.Status};
        if(value.FailureReason!=null)description.Add(value.FailureReason);
        if(value.Status=="Live"&&value.Snapshot?.GpuStatus.StartsWith("worker_",StringComparison.Ordinal)==true)description.Add(GpuStatus(value.Snapshot.GpuStatus));
        if(value.LastSuccess.HasValue)description.Add($"Last reading {value.LastSuccess.Value.ToLocalTime():HH:mm:ss}");
        status.AccessibleDescription=string.Join(". ",description);tooltip.SetToolTip(status,status.AccessibleDescription);
        cpu.Value=value.Cpu;gpu.Value=value.Gpu;gpu.Partial=value.PartialGpu;ram.Value=value.Ram;
        history.UpdateHistory(value.History);
        foreach(var meter in new[]{cpu,gpu,ram})meter.Invalidate();
        UpdateDetails();
    }
    public static string Memory(MemoryReading? m)=>m?.UsedBytes is ulong used && m.TotalBytes is ulong total && m.Status=="ok" && m.AgeMs<=10000
        ?$"{used/1073741824.0:0.0} / {total/1073741824.0:0.#} GiB":"Unavailable";
    static string GpuStatus(string value)=>value switch
    {
        "worker_exited"=>"GPU worker exited; retrying",
        "worker_timeout"=>"GPU worker stopped responding; retrying",
        "worker_invalid_output"=>"GPU worker returned an invalid sample; retrying",
        "worker_start_failed"=>"GPU worker could not start; retrying",
        _=>"GPU monitoring unavailable: "+value.Replace('_',' ')
    };
    void UpdateDetails()
    {
        title.Text=$"{(expanded?"▾":"▸")}  {state.Peer.Name}{(state.Peer.IsSelf?"  · this PC":"")}";
        detail.Visible=history.Visible=expanded;if(!expanded){Height=S(88);return;}
        var s=state.Snapshot;
        var lines=new List<string>{state.Peer.Os+"  ·  "+string.Join(", ",state.Peer.Addresses)};
        if(s!=null && state.Status=="Live")
        {
            lines.Add($"{s.Cpu.Model}  ·  {s.Cpu.LogicalCores} logical CPUs");lines.Add("RAM  "+Memory(s.Ram));
            if(s.Gpus.Count==0)lines.Add(GpuStatus(s.GpuStatus));
            foreach(var g in s.Gpus)
            {
                lines.Add($"{g.Name}  ·  {(g.Utilization.IsFresh?$"{g.Utilization.Value:0}% active":"Unavailable")}");
                if(g.Memory!=null)lines.Add("Video memory  "+Memory(g.Memory));
                else if(g.Vendor=="Apple")lines.Add("GPU uses shared unified memory.");
            }
            if(s.Gpus.Count>1)lines.Add("Summary shows the busiest GPU"+(state.PartialGpu?" (partial readings).":"."));
        }
        else
        {
            if(state.FailureReason!=null)lines.Add(state.FailureReason);
            lines.Add(state.Status switch {
                "Agent unavailable"=>"Install and start the Tailwatch agent on this machine.",
                "Access denied"=>"Add this viewer's Tailscale address to the agent's allowed peers.",
                "Not monitored"=>"Enable this machine in Machines to collect readings.",
                "Connecting"=>"Waiting for a fresh agent sample.",
                "Offline"=>"This device is offline in Tailscale.",
                _=>"No fresh readings. Check the agent and Tailscale connection."});
        }
        if(state.LastSuccess.HasValue)lines.Add($"Last reading  {state.LastSuccess.Value.ToLocalTime():HH:mm:ss}");
        detail.Text=string.Join(Environment.NewLine,lines);
        detail.Height=Math.Max(S(70),TextRenderer.MeasureText(detail.Text,Font,new Size(Math.Max(S(180),Width-S(28)),S(1000)),TextFormatFlags.WordBreak).Height+S(24));
        history.SetBounds(S(4),detail.Bottom+S(6),Width-S(8),S(124));
        Height=history.Bottom+S(12);
    }
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);using var pen=new Pen(Theme.Border);e.Graphics.DrawLine(pen,0,0,Width,0);}
    protected override void Dispose(bool disposing){if(disposing){tooltip.Dispose();title.Font.Dispose();}base.Dispose(disposing);}
}
