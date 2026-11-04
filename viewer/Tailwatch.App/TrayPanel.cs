using Tailwatch.Core;
namespace Tailwatch.App;
internal sealed class TrayPanel : Form
{
    readonly Button subtitle=Theme.Button("Connecting to Tailscale…  ▾",(_,_)=>{});
    readonly Label summary=new(),footer=new();
    readonly SearchField searchField=new();
    TextBox search=>searchField.Editor;
    readonly Button sort=Theme.Button("Sort: Name  ▾",(_,_)=>{});
    readonly ContextMenuStrip sortMenu=Theme.Menu();
    readonly ContextMenuStrip accountMenu=Theme.Menu();
    readonly Button reporting=Theme.Button("Reporting only",(_,_)=>{});
    readonly MachineViewport viewport=new();
    FlowLayoutPanel list=>viewport.List;
    readonly Label emptyState=new(){ForeColor=Theme.Muted,AutoSize=false,Size=new Size(440,80),Padding=new Padding(6,20,0,0),Visible=false};
    readonly Panel offline=new();
    readonly Button settings,refresh;
    string sortValue="Name";
    bool connectionError;
    bool reportingOnly;
    string? switchingTo;
    readonly Dictionary<string,MachineCard> cards=[];
    IReadOnlyList<MachineState> machines=[];
    public event Action? RefreshRequested,SettingsRequested,SortChanged,OpenTailscaleRequested,FilterChanged,AccountsRequested;
    public event Action<TailscaleAccount>? AccountSwitchRequested;
    public string SortValue=>sortValue;
    public bool ReportingOnly=>reportingOnly;
    public TrayPanel()
    {
        SuspendLayout();
        Text="Tailwatch";Name="TailwatchPanel";ShowInTaskbar=false;FormBorderStyle=FormBorderStyle.None;
        StartPosition=FormStartPosition.Manual;
        MaximizeBox=false;MinimizeBox=false;ControlBox=false;TopMost=true;KeyPreview=true;
        BackColor=Theme.Background;ForeColor=Theme.Text;Font=new Font("Segoe UI",9f);
        AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;ClientSize=new Size(520,560);
        var heading=new Label{Text="Tailwatch",Font=new Font("Segoe UI",16f,FontStyle.Bold),Location=new Point(18,14),AutoSize=true};
        subtitle.SetBounds(18,43,440,29);subtitle.AutoSize=false;subtitle.ForeColor=Theme.Muted;subtitle.AutoEllipsis=true;
        subtitle.UseMnemonic=false;subtitle.TextAlign=ContentAlignment.MiddleLeft;subtitle.BackColor=Theme.Background;
        subtitle.FlatAppearance.BorderSize=0;subtitle.Padding=new Padding(2,0,2,0);subtitle.AccessibleName="Switch tailnet";
        subtitle.ContextMenuStrip=accountMenu;
        subtitle.Click+=(_,_)=>accountMenu.Show(subtitle,new Point(0,subtitle.Height+3));
        accountMenu.Opening+=(_,e)=>{SetAccounts(null);e.Cancel=false;AccountsRequested?.Invoke();};
        accountMenu.Closed+=MenuClosed;
        var close=new PanelCloseButton();close.Click+=(_,_)=>Hide();close.SetBounds(467,15,32,32);
        searchField.SetBounds(20,79,320,32);
        sort.SetBounds(350,79,150,32);sort.AutoSize=false;sort.AccessibleName="Sort machines";sort.TextAlign=ContentAlignment.MiddleLeft;
        foreach(var value in new[]{"Name","CPU","GPU","RAM"})
        {
            var item=new ToolStripMenuItem(value){Checked=value==sortValue,ForeColor=Theme.Text,Padding=new Padding(10,7,18,7)};
            item.Click+=(_,_)=>SetSort(value);sortMenu.Items.Add(item);
        }
        sort.ContextMenuStrip=sortMenu;
        sort.Click+=(_,_)=>sortMenu.Show(sort,new Point(0,sort.Height+4));
        sortMenu.Closed+=MenuClosed;
        search.TextChanged+=(_,_)=>RenderRows();
        summary.SetBounds(20,123,330,22);summary.ForeColor=Theme.Muted;summary.AutoEllipsis=true;
        reporting.SetBounds(362,119,138,27);reporting.AutoSize=false;reporting.AccessibleName="Reporting only";
        reporting.Font=new Font("Segoe UI",8f);reporting.Padding=new Padding(2);reporting.Click+=(_,_)=>SetReportingOnly(!reportingOnly);
        viewport.SetBounds(16,150,488,340);
        viewport.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right|AnchorStyles.Bottom;
        list.Controls.Add(emptyState);
        offline.SetBounds(20,150,480,340);offline.Anchor=viewport.Anchor;offline.Visible=false;
        var offlineTitle=new Label{Text="Connect to your machines",Font=new Font("Segoe UI",13f,FontStyle.Bold),TextAlign=ContentAlignment.MiddleCenter,Bounds=new Rectangle(10,65,460,34),Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right};
        var offlineHelp=new Label{Text="Open Tailscale and connect to your tailnet.\nYour machines will appear here automatically.",ForeColor=Theme.Muted,TextAlign=ContentAlignment.MiddleCenter,Bounds=new Rectangle(10,110,460,58),Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right};
        var launch=Theme.Button("Open Tailscale",(_,_)=>OpenTailscaleRequested?.Invoke());launch.SetBounds(160,188,160,38);launch.AutoSize=false;launch.BackColor=Theme.Accent;launch.ForeColor=Theme.Background;launch.FlatAppearance.BorderSize=0;launch.Anchor=AnchorStyles.Top;
        offline.Controls.AddRange([offlineTitle,offlineHelp,launch]);
        settings=Theme.Button("Machines",(_,_)=>{if(connectionError)OpenTailscaleRequested?.Invoke();else SettingsRequested?.Invoke();});settings.SetBounds(18,509,110,32);settings.AutoSize=false;settings.Anchor=AnchorStyles.Bottom|AnchorStyles.Left;
        refresh=Theme.Button("Refresh",(_,_)=>RefreshRequested?.Invoke());refresh.SetBounds(402,509,98,32);refresh.AutoSize=false;refresh.Anchor=AnchorStyles.Bottom|AnchorStyles.Right;
        footer.SetBounds(140,509,250,32);footer.ForeColor=Theme.Muted;footer.Font=new Font("Segoe UI",8f);footer.TextAlign=ContentAlignment.MiddleCenter;footer.Anchor=AnchorStyles.Left|AnchorStyles.Bottom|AnchorStyles.Right;footer.AutoEllipsis=true;
        Controls.AddRange([heading,subtitle,close,searchField,sort,summary,reporting,viewport,offline,settings,refresh,footer]);
        Deactivate+=(_,_)=>{if(!sortMenu.Visible&&!accountMenu.Visible)Hide();};KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Escape){Hide();e.Handled=true;}};
        FormClosing+=(_,e)=>{if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}};
        ResumeLayout(true);
    }
    void MenuClosed(object? sender,ToolStripDropDownClosedEventArgs e)
    {
        if(IsHandleCreated&&!IsDisposed)BeginInvoke((Action)(()=>{if(!IsDisposed&&!ContainsFocus&&!accountMenu.Visible&&!sortMenu.Visible)Hide();}));
    }
    public void SetReportingOnly(bool enabled)
    {
        if(reportingOnly==enabled)return;
        reportingOnly=enabled;reporting.BackColor=enabled?Theme.Accent:Theme.Card;reporting.ForeColor=enabled?Theme.Background:Theme.Muted;
        reporting.Text=enabled?"Reporting only: on":"Reporting only";
        reporting.AccessibleDescription=enabled?"Showing only machines with fresh Tailwatch readings":"Showing all machines";
        RenderRows();FilterChanged?.Invoke();
    }
    public void SetAccounts(IReadOnlyList<TailscaleAccount>? accounts,string? error=null)
    {
        foreach(var item in accountMenu.Items.Cast<ToolStripItem>().ToArray()){accountMenu.Items.Remove(item);item.Dispose();}
        if(accounts==null||accounts.Count==0)
            accountMenu.Items.Add(new ToolStripMenuItem(error??(accounts==null?"Loading accounts…":"No saved Tailscale accounts")){Enabled=false});
        else foreach(var account in accounts)
        {
            var item=new ToolStripMenuItem(account.Label){Checked=account.Selected,Enabled=!account.Selected,Padding=new Padding(10,7,18,7)};
            // Escape menu mnemonics so account names containing '&' remain literal.
            item.Text=account.Label.Replace("&","&&");
            item.Click+=(_,_)=>AccountSwitchRequested?.Invoke(account);accountMenu.Items.Add(item);
        }
        accountMenu.Items.Add(new ToolStripSeparator());
        accountMenu.Items.Add("Manage accounts in Tailscale…",null,(_,_)=>OpenTailscaleRequested?.Invoke());
    }
    public void SetSwitching(string? target){switchingTo=target;}
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
        bool switching=switchingTo!=null;
        connectionError=error!=null;bool emptyOffline=connectionError&&values.Count==0&&!switching;
        subtitle.Text=switching?"Switching to "+switchingTo+"…":(emptyOffline?"Tailscale is disconnected":string.IsNullOrEmpty(network)?"Connecting to Tailscale…":network+(error==null?"  ·  active tailnet":"  ·  last known tailnet"))+"  ▾";
        summary.Text=switching?"Connecting to the selected tailnet…":error??$"{values.Count(m=>m.Peer.Online)} online  ·  {values.Count(m=>!m.Peer.Online)} offline  ·  {values.Count(m=>m.Status=="Live")} reporting";
        subtitle.Enabled=refresh.Enabled=!switching;
        searchField.Enabled=sort.Enabled=settings.Enabled=reporting.Enabled=!emptyOffline&&!switching;
        searchField.Visible=sort.Visible=settings.Visible=reporting.Visible=!emptyOffline&&!switching;
        settings.Text=connectionError&&!emptyOffline?"Open Tailscale":"Machines";
        refresh.Text=connectionError?"Retry":"Refresh";
        offline.Visible=emptyOffline;viewport.Visible=!emptyOffline;
        footer.Text=switching?"Switching this computer’s connection":connectionError?"Waiting for Tailscale":"Updates every 2s while open";RenderRows();
    }
    void RenderRows()
    {
        if(IsDisposed)return;
        var query=search.Text.Trim();
        var shown=machines.Where(m=>(!reportingOnly||m.Status=="Live")&&(m.Peer.Name+" "+m.Peer.Os+" "+string.Join(" ",m.Snapshot?.Gpus.Select(g=>g.Name)??[])).Contains(query,StringComparison.OrdinalIgnoreCase));
        double? Load(MachineState m)=>SortValue switch{"CPU"=>m.Cpu,"GPU"=>m.Gpu,"RAM"=>m.Ram,_=>null};
        shown=SortValue=="Name"?shown.OrderByDescending(m=>m.Peer.Online).ThenBy(m=>m.Peer.Name):shown.OrderByDescending(m=>Load(m).HasValue).ThenByDescending(Load).ThenBy(m=>m.Peer.Name);
        var rows=shown.ToList();var scroll=list.AutoScrollPosition;
        var present=machines.Select(m=>m.Peer.Id).ToHashSet();
        var visible=rows.Select(m=>m.Peer.Id).ToHashSet();
        list.SuspendLayout();
        try
        {
            foreach(var obsolete in cards.Keys.Where(id=>!present.Contains(id)).ToArray()){cards[obsolete].Dispose();cards.Remove(obsolete);}
            // Only hide rows removed by the filter. Refreshing a reading must never
            // hide its existing control, which briefly erased the entire fleet.
            foreach(var pair in cards)if(!visible.Contains(pair.Key))pair.Value.Visible=false;
            int index=0;
            foreach(var row in rows)
            {
                if(!cards.TryGetValue(row.Peer.Id,out var card)){card=new MachineCard(row);cards[row.Peer.Id]=card;list.Controls.Add(card);}
                card.Width=viewport.ContentWidth;
                card.UpdateState(row);card.Visible=true;
                if(list.Controls.GetChildIndex(card)!=index)list.Controls.SetChildIndex(card,index);
                index++;
            }
            emptyState.Text=switchingTo!=null?"Waiting for the new machine list…":reportingOnly&&query.Length==0?"No machines are reporting yet. Turn off Reporting only to see all machines.":machines.Count==0?"No machines yet. Connect Tailscale to get started.":"No matching machines.";
            emptyState.Width=viewport.ContentWidth;
            emptyState.Visible=rows.Count==0;
        }
        finally{list.ResumeLayout(true);}
        if(list.AutoScrollPosition!=scroll)list.AutoScrollPosition=new Point(-scroll.X,-scroll.Y);
        viewport.SyncScroll();
    }
    public void ShowNearTray()
    {
        var work=Screen.FromPoint(Cursor.Position).WorkingArea;
        Location=new Point(Math.Max(work.Left,work.Right-Width-12),Math.Max(work.Top,work.Bottom-Height-12));
        Show();Activate();
    }
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);using var pen=new Pen(Theme.Border);e.Graphics.DrawRectangle(pen,0,0,ClientSize.Width-1,ClientSize.Height-1);}
    protected override void Dispose(bool disposing){if(disposing){sortMenu.Dispose();accountMenu.Dispose();}base.Dispose(disposing);}
}
internal sealed class BufferedMachineList : FlowLayoutPanel
{
    public BufferedMachineList(){DoubleBuffered=true;ResizeRedraw=true;}
    protected override void WndProc(ref Message message)
    {
        // WM_PRINT does not honor the parent viewport's clipping like screen
        // painting does. Exclude the hidden native non-client scrollbar in captures.
        if(message.Msg==0x0317)message.LParam=(IntPtr)(message.LParam.ToInt64()&~0x02L);
        base.WndProc(ref message);
    }
}
