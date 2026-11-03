using Tailwatch.Core;
namespace Tailwatch.App;
internal sealed class TrayPanel : Form
{
    readonly Label subtitle=new(),summary=new(),footer=new();
    readonly TextBox search=new();
    readonly Button sort=Theme.Button("Sort: Name  ▾",(_,_)=>{});
    readonly ContextMenuStrip sortMenu=Theme.Menu();
    readonly FlowLayoutPanel list=new();
    readonly Panel offline=new();
    readonly Button settings,refresh;
    string sortValue="Name";
    bool connectionError;
    readonly Dictionary<string,MachineCard> cards=[];
    IReadOnlyList<MachineState> machines=[];
    public event Action? RefreshRequested,SettingsRequested,SortChanged,OpenTailscaleRequested;
    public string SortValue=>sortValue;
    public TrayPanel()
    {
        SuspendLayout();
        Text="Tailwatch";Name="TailwatchPanel";ShowInTaskbar=false;FormBorderStyle=FormBorderStyle.None;
        MaximizeBox=false;MinimizeBox=false;ControlBox=false;TopMost=true;KeyPreview=true;
        BackColor=Theme.Background;ForeColor=Theme.Text;Font=new Font("Segoe UI",9f);
        AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;ClientSize=new Size(520,560);
        var heading=new Label{Text="Tailwatch",Font=new Font("Segoe UI",16f,FontStyle.Bold),Location=new Point(18,14),AutoSize=true};
        subtitle.SetBounds(20,48,450,20);subtitle.ForeColor=Theme.Muted;subtitle.AutoEllipsis=true;
        var close=Theme.Button("×",(_,_)=>Hide());close.SetBounds(467,15,32,30);
        search.PlaceholderText="Find a machine…";search.AccessibleName="Find a machine";search.SetBounds(20,81,320,28);
        search.BackColor=Theme.Card;search.ForeColor=Theme.Text;search.BorderStyle=BorderStyle.FixedSingle;
        sort.SetBounds(350,79,150,32);sort.AutoSize=false;sort.AccessibleName="Sort machines";sort.TextAlign=ContentAlignment.MiddleLeft;
        foreach(var value in new[]{"Name","CPU","GPU","RAM"})
        {
            var item=new ToolStripMenuItem(value){Checked=value==sortValue,ForeColor=Theme.Text,Padding=new Padding(10,7,18,7)};
            item.Click+=(_,_)=>SetSort(value);sortMenu.Items.Add(item);
        }
        sort.ContextMenuStrip=sortMenu;
        sort.Click+=(_,_)=>sortMenu.Show(sort,new Point(0,sort.Height+4));
        sortMenu.Closed+=(_,_)=>{if(IsHandleCreated&&!IsDisposed)BeginInvoke((Action)(()=>{if(!IsDisposed&&!ContainsFocus)Hide();}));};
        search.TextChanged+=(_,_)=>RenderRows();
        summary.SetBounds(20,121,480,22);summary.ForeColor=Theme.Muted;summary.AutoEllipsis=true;
        list.SetBounds(16,150,488,340);list.FlowDirection=FlowDirection.TopDown;list.WrapContents=false;list.AutoScroll=true;
        list.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right|AnchorStyles.Bottom;
        offline.SetBounds(20,150,480,340);offline.Anchor=list.Anchor;offline.Visible=false;
        var offlineTitle=new Label{Text="Connect to your machines",Font=new Font("Segoe UI",13f,FontStyle.Bold),TextAlign=ContentAlignment.MiddleCenter,Bounds=new Rectangle(10,65,460,34),Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right};
        var offlineHelp=new Label{Text="Open Tailscale and connect to your tailnet.\nYour machines will appear here automatically.",ForeColor=Theme.Muted,TextAlign=ContentAlignment.MiddleCenter,Bounds=new Rectangle(10,110,460,58),Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right};
        var launch=Theme.Button("Open Tailscale",(_,_)=>OpenTailscaleRequested?.Invoke());launch.SetBounds(160,188,160,38);launch.AutoSize=false;launch.BackColor=Theme.Accent;launch.ForeColor=Theme.Background;launch.FlatAppearance.BorderSize=0;launch.Anchor=AnchorStyles.Top;
        offline.Controls.AddRange([offlineTitle,offlineHelp,launch]);
        settings=Theme.Button("Machines",(_,_)=>{if(connectionError)OpenTailscaleRequested?.Invoke();else SettingsRequested?.Invoke();});settings.SetBounds(18,509,110,32);settings.AutoSize=false;settings.Anchor=AnchorStyles.Bottom|AnchorStyles.Left;
        refresh=Theme.Button("Refresh",(_,_)=>RefreshRequested?.Invoke());refresh.SetBounds(402,509,98,32);refresh.AutoSize=false;refresh.Anchor=AnchorStyles.Bottom|AnchorStyles.Right;
        footer.SetBounds(140,509,250,32);footer.ForeColor=Theme.Muted;footer.Font=new Font("Segoe UI",8f);footer.TextAlign=ContentAlignment.MiddleCenter;footer.Anchor=AnchorStyles.Left|AnchorStyles.Bottom|AnchorStyles.Right;footer.AutoEllipsis=true;
        Controls.AddRange([heading,subtitle,close,search,sort,summary,list,offline,settings,refresh,footer]);
        Deactivate+=(_,_)=>{if(!sortMenu.Visible)Hide();};KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Escape){Hide();e.Handled=true;}};
        FormClosing+=(_,e)=>{if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}};
        ResumeLayout(true);
    }
    public void SetSort(string value)
    {
        if(!new[]{"Name","CPU","GPU","RAM"}.Contains(value)||sortValue==value)return;
        sortValue=value;sort.Text="Sort: "+value+"  ▾";
        foreach(ToolStripMenuItem item in sortMenu.Items)item.Checked=item.Text==value;
        RenderRows();SortChanged?.Invoke();
    }
    public void UpdateMachines(string network,IReadOnlyList<MachineState> values,string? error)
    {
        machines=values;
        connectionError=error!=null;bool emptyOffline=connectionError&&values.Count==0;
        subtitle.Text=emptyOffline?"Tailscale is disconnected":string.IsNullOrEmpty(network)?"Connecting to Tailscale…":network+(error==null?"  ·  active tailnet":"  ·  last known tailnet");
        summary.Text=error??$"{values.Count(m=>m.Peer.Online)} online  ·  {values.Count(m=>!m.Peer.Online)} offline  ·  {values.Count(m=>m.Status=="Live")} reporting";
        search.Enabled=sort.Enabled=!emptyOffline;settings.Enabled=!emptyOffline;
        search.Visible=sort.Visible=settings.Visible=!emptyOffline;
        settings.Text=connectionError&&!emptyOffline?"Open Tailscale":"Machines";
        refresh.Text=connectionError?"Retry":"Refresh";
        offline.Visible=emptyOffline;list.Visible=!emptyOffline;
        footer.Text=connectionError?"Waiting for Tailscale":"Updates every 2s while open";RenderRows();
    }
    void RenderRows()
    {
        if(IsDisposed)return;
        var query=search.Text.Trim();
        var shown=machines.Where(m=>(m.Peer.Name+" "+m.Peer.Os+" "+string.Join(" ",m.Snapshot?.Gpus.Select(g=>g.Name)??[])).Contains(query,StringComparison.OrdinalIgnoreCase));
        double? Load(MachineState m)=>SortValue switch{"CPU"=>m.Cpu,"GPU"=>m.Gpu,"RAM"=>m.Ram,_=>null};
        shown=SortValue=="Name"?shown.OrderByDescending(m=>m.Peer.Online).ThenBy(m=>m.Peer.Name):shown.OrderByDescending(m=>Load(m).HasValue).ThenByDescending(Load).ThenBy(m=>m.Peer.Name);
        var rows=shown.ToList();var scroll=list.AutoScrollPosition;list.SuspendLayout();
        foreach(var obsolete in cards.Keys.Where(id=>!machines.Any(m=>m.Peer.Id==id)).ToArray()){cards[obsolete].Dispose();cards.Remove(obsolete);}
        foreach(var label in list.Controls.OfType<Label>().ToArray())label.Dispose();
        foreach(var card in cards.Values)card.Visible=false;
        int index=0;
        foreach(var row in rows)
        {
            if(!cards.TryGetValue(row.Peer.Id,out var card)){card=new MachineCard(row);cards[row.Peer.Id]=card;list.Controls.Add(card);}
            card.Width=list.ClientSize.Width-SystemInformation.VerticalScrollBarWidth-4;card.UpdateState(row);card.Visible=true;list.Controls.SetChildIndex(card,index++);
        }
        if(rows.Count==0)list.Controls.Add(new Label{Text=machines.Count==0?"No machines yet. Connect Tailscale to get started.":"No matching machines.",ForeColor=Theme.Muted,AutoSize=false,Size=new Size(440,80),Padding=new Padding(6,20,0,0)});
        list.ResumeLayout();list.AutoScrollPosition=new Point(-scroll.X,-scroll.Y);
    }
    public void ShowNearTray()
    {
        var work=Screen.FromPoint(Cursor.Position).WorkingArea;
        Location=new Point(Math.Max(work.Left,work.Right-Width-12),Math.Max(work.Top,work.Bottom-Height-12));
        Show();Activate();
    }
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);using var pen=new Pen(Theme.Border);e.Graphics.DrawRectangle(pen,0,0,ClientSize.Width-1,ClientSize.Height-1);}
    protected override void Dispose(bool disposing){if(disposing)sortMenu.Dispose();base.Dispose(disposing);}
}
