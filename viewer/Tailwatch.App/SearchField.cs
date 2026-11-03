using System.Drawing.Drawing2D;
namespace Tailwatch.App;

internal sealed class SearchField : UserControl
{
    public TextBox Editor {get;}=new(){BorderStyle=BorderStyle.None,BackColor=Theme.Card,ForeColor=Theme.Text,
        PlaceholderText="Find a machine…",AccessibleName="Find a machine"};
    int S(int value)=>(int)Math.Round(value*DeviceDpi/96f);
    public SearchField()
    {
        AutoScaleMode=AutoScaleMode.None;DoubleBuffered=true;BackColor=Theme.Card;TabStop=false;
        Controls.Add(Editor);Editor.Enter+=(_,_)=>Invalidate();Editor.Leave+=(_,_)=>Invalidate();
        Click+=(_,_)=>Editor.Focus();Cursor=Cursors.IBeam;
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if(Editor==null)return;
        int height=Editor.PreferredHeight;
        Editor.SetBounds(S(34),Math.Max(0,(Height-height)/2),Math.Max(0,Width-S(46)),height);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using var border=new Pen(ContainsFocus?Theme.Accent:Theme.Border,ContainsFocus?1.4f*DeviceDpi/96f:1f);
        e.Graphics.DrawRectangle(border,1,1,Math.Max(0,Width-3),Math.Max(0,Height-3));
        float radius=S(4),x=S(16),y=Height/2f-S(2);
        using var icon=new Pen(ContainsFocus?Theme.Text:Theme.Muted,1.5f*DeviceDpi/96f){StartCap=LineCap.Round,EndCap=LineCap.Round};
        e.Graphics.DrawEllipse(icon,x-radius,y-radius,radius*2,radius*2);
        e.Graphics.DrawLine(icon,x+radius*0.7f,y+radius*0.7f,x+radius+S(3),y+radius+S(3));
    }
}
