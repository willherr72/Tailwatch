using Tailwatch.Core;
namespace Tailwatch.App;
internal sealed class TrayPanel : Form
{
    readonly Label subtitle=new(),summary=new(),footer=new();
    readonly TextBox search=new();
    readonly ComboBox sort=new();
    readonly FlowLayoutPanel list=new();
    readonly Dictionary<string,MachineCard> cards=[];
    IReadOnlyList<MachineState> machines=[];
    public event Action? RefreshRequested,SettingsRequested,SortChanged;
    public string SortValue=>sort.SelectedItem?.ToString()??"Name";
    public TrayPanel()
    {
        Text="Tailwatch";Name="TailwatchPanel";ShowInTaskbar=false;FormBorderStyle=FormBorderStyle.None;
        MaximizeBox=false;MinimizeBox=false;ControlBox=false;TopMost=true;KeyPreview=true;
        BackColor=Theme.Background;ForeColor=Theme.Text;Font=new Font("Segoe UI",9f);
        AutoScaleMode=AutoScaleMode.Dpi;ClientSize=new Size(520,560);
        var heading=new Label{Text="Tailwatch",Font=new Font("Segoe UI",16f,FontStyle.Bold),Location=new Point(18,14),Size=new Size(390,30)};
        subtitle.SetBounds(20,48,450,20);subtitle.ForeColor=Theme.Muted;subtitle.AutoEllipsis=true;
        var close=Theme.Button("×",(_,_)=>Hide());close.SetBounds(467,15,32,30);
        search.PlaceholderText="Find a machine…";search.AccessibleName="Find a machine";search.SetBounds(20,81,320,28);
        search.BackColor=Theme.Card;search.ForeColor=Theme.Text;search.BorderStyle=BorderStyle.FixedSingle;
        sort.SetBounds(350,81,150,28);sort.DropDownStyle=ComboBoxStyle.DropDownList;sort.Items.AddRange(["Name","CPU","GPU","RAM"]);sort.SelectedIndex=0;sort.AccessibleName="Sort machines";
        sort.BackColor=Theme.Card;sort.ForeColor=Theme.Text;sort.FlatStyle=FlatStyle.Flat;sort.DrawMode=DrawMode.OwnerDrawFixed;
        sort.DrawItem+=(_,e)=>{e.DrawBackground();if(e.Index>=0)TextRenderer.DrawText(e.Graphics,sort.Items[e.Index]?.ToString(),Font,e.Bounds,Theme.Text,TextFormatFlags.Left|TextFormatFlags.VerticalCenter);};
        search.TextChanged+=(_,_)=>RenderRows();sort.SelectedIndexChanged+=(_,_)=>{RenderRows();SortChanged?.Invoke();};
        summary.SetBounds(20,121,480,22);summary.ForeColor=Theme.Muted;summary.AutoEllipsis=true;
        list.SetBounds(16,150,488,340);list.FlowDirection=FlowDirection.TopDown;list.WrapContents=false;list.AutoScroll=true;
        list.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right|AnchorStyles.Bottom;
        var settings=Theme.Button("Machines",(_,_)=>SettingsRequested?.Invoke());settings.SetBounds(18,509,96,30);settings.Anchor=AnchorStyles.Bottom|AnchorStyles.Left;
        var refresh=Theme.Button("Refresh",(_,_)=>RefreshRequested?.Invoke());refresh.SetBounds(402,509,98,30);refresh.Anchor=AnchorStyles.Bottom|AnchorStyles.Right;
        footer.SetBounds(126,516,265,22);footer.ForeColor=Theme.Muted;footer.Font=new Font("Segoe UI",8f);footer.Anchor=AnchorStyles.Left|AnchorStyles.Bottom|AnchorStyles.Right;footer.AutoEllipsis=true;
        Controls.AddRange([heading,subtitle,close,search,sort,summary,list,settings,refresh,footer]);
        Deactivate+=(_,_)=>Hide();KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Escape){Hide();e.Handled=true;}};
        FormClosing+=(_,e)=>{if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}};
    }
    public void SetSort(string value){if(sort.Items.Contains(value))sort.SelectedItem=value;}
    public void UpdateMachines(string network,IReadOnlyList<MachineState> values,string? error)
    {
        machines=values;
        subtitle.Text=string.IsNullOrEmpty(network)?"Connecting to Tailscale…":network+(error==null?"  ·  active tailnet":"  ·  last known tailnet");
        summary.Text=error??$"{values.Count(m=>m.Peer.Online)} online  ·  {values.Count(m=>!m.Peer.Online)} offline  ·  {values.Count(m=>m.Status=="Live")} reporting";
        footer.Text=error!=null?"Connection unavailable":"Updates every 2s while open";RenderRows();
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
}
