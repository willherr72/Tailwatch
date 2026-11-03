using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
namespace Tailwatch.App;

internal sealed class MachineViewport : Panel
{
    readonly Panel clip=new(){TabStop=false};
    readonly DarkScrollBar scrollbar=new();
    bool queued;
    public FlowLayoutPanel List {get;}=new BufferedMachineList(){FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Margin=Padding.Empty};
    public int ContentWidth=>Math.Max(0,clip.ClientSize.Width-4);
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index,uint dpi);
    int S(int value)=>(int)Math.Round(value*DeviceDpi/96f);
    public MachineViewport()
    {
        DoubleBuffered=true;BackColor=Theme.Background;TabStop=false;
        clip.Controls.Add(List);Controls.AddRange([clip,scrollbar]);
        List.Scroll+=(_,_)=>SyncScroll();
        List.Layout+=(_,_)=>QueueSync();
        scrollbar.ScrollRequested+=value=>{List.AutoScrollPosition=new Point(0,value);SyncScroll();};
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if(clip==null)return;
        clip.SetBounds(0,0,Math.Max(0,Width-S(14)),Height);
        scrollbar.SetBounds(clip.Right,0,S(14),Height);
        // Retain native wheel, focus and scrolling behavior. Its light non-client
        // scrollbar sits outside this clipped viewport; our thumb controls the same offset.
        List.SetBounds(0,0,clip.Width+GetSystemMetricsForDpi(2,(uint)DeviceDpi),Height);
        foreach(Control child in List.Controls)child.Width=ContentWidth;
        SyncScroll();
    }
    void QueueSync()
    {
        if(queued||!IsHandleCreated||IsDisposed)return;
        queued=true;BeginInvoke((Action)(()=>{queued=false;if(!IsDisposed)SyncScroll();}));
    }
    public void SyncScroll()=>scrollbar.SetMetrics(-List.AutoScrollPosition.Y,List.ClientSize.Height,List.DisplayRectangle.Height);
}

internal sealed class DarkScrollBar : Control
{
    int offset,viewport,content,dragOffset,wheelRemainder;
    bool dragging,hovered;
    int Maximum=>Math.Max(0,content-viewport);
    int S(int value)=>(int)Math.Round(value*DeviceDpi/96f);
    Rectangle Thumb
    {
        get
        {
            if(Maximum==0||Height==0)return Rectangle.Empty;
            int height=Math.Min(Height,Math.Max(S(32),(int)((long)Height*viewport/Math.Max(1,content))));
            int y=(int)((long)(Height-height)*offset/Maximum);
            int width=S(6);
            return new((Width-width)/2,y,width,height);
        }
    }
    public event Action<int>? ScrollRequested;
    public DarkScrollBar()
    {
        DoubleBuffered=true;ResizeRedraw=true;BackColor=Theme.Background;Cursor=Cursors.Hand;
        AccessibleName="Machine list scrollbar";AccessibleRole=AccessibleRole.ScrollBar;
        AccessibleDescription="Use the arrow keys, Page Up, Page Down, Home, or End to scroll machines.";
    }
    public void SetMetrics(int value,int page,int total)
    {
        int bounded=Math.Clamp(value,0,Math.Max(0,total-page));
        if(offset==bounded&&viewport==page&&content==total)return;
        offset=bounded;viewport=page;content=total;Enabled=TabStop=Maximum>0;Invalidate();
        AccessibilityNotifyClients(AccessibleEvents.ValueChange,-1);
    }
    void MoveTo(int value)=>ScrollRequested?.Invoke(Math.Clamp(value,0,Maximum));
    protected override bool IsInputKey(Keys keyData)=>keyData is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        int? target=e.KeyCode switch{Keys.Up=>offset-S(24),Keys.Down=>offset+S(24),Keys.PageUp=>offset-viewport,Keys.PageDown=>offset+viewport,Keys.Home=>0,Keys.End=>Maximum,_=>null};
        if(target.HasValue){MoveTo(target.Value);e.Handled=true;e.SuppressKeyPress=true;}
        base.OnKeyDown(e);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);if(e.Button!=MouseButtons.Left||Maximum==0)return;
        Focus();var thumb=Thumb;
        if(e.Y>=thumb.Top&&e.Y< thumb.Bottom){dragging=true;dragOffset=e.Y-thumb.Top;Capture=true;Invalidate();}
        else MoveTo(offset+(e.Y<thumb.Top?-viewport:viewport));
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool over=Thumb.Contains(e.Location);if(over!=hovered){hovered=over;Invalidate();}
        if(dragging)
        {
            int travel=Height-Thumb.Height;
            if(travel>0)MoveTo((int)((long)Math.Clamp(e.Y-dragOffset,0,travel)*Maximum/travel));
        }
    }
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);dragging=false;Capture=false;Invalidate();}
    protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture){dragging=false;Invalidate();}}
    protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hovered=false;Invalidate();}
    protected override void OnGotFocus(EventArgs e){base.OnGotFocus(e);Invalidate();}
    protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);Invalidate();}
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);wheelRemainder+=e.Delta;
        int steps=wheelRemainder/SystemInformation.MouseWheelScrollDelta;wheelRemainder%=SystemInformation.MouseWheelScrollDelta;
        int lines=SystemInformation.MouseWheelScrollLines;
        MoveTo(offset-steps*(lines<0?viewport:S(20)*lines));
        if(e is HandledMouseEventArgs handled)handled.Handled=true;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);var thumb=Thumb;if(thumb.IsEmpty)return;
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using var brush=new SolidBrush(dragging||Focused?Theme.Accent:hovered?Theme.Muted:Color.FromArgb(68,79,90));
        using var path=new GraphicsPath();float radius=thumb.Width;
        path.AddArc(thumb.Left,thumb.Top,radius,radius,180,180);
        path.AddArc(thumb.Left,thumb.Bottom-radius,radius,radius,0,180);path.CloseFigure();e.Graphics.FillPath(brush,path);
    }
    protected override AccessibleObject CreateAccessibilityInstance()=>new ScrollAccessibility(this);
    sealed class ScrollAccessibility(DarkScrollBar owner):ControlAccessibleObject(owner)
    {
        public override string? Value {get=>$"{owner.offset} of {owner.Maximum}";set{if(int.TryParse(value,out var position))owner.MoveTo(position);}}
    }
}
