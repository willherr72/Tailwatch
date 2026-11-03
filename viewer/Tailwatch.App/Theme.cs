using System.Runtime.InteropServices;
namespace Tailwatch.App;
internal static class Theme
{
    public static readonly Color Background=Color.FromArgb(20,23,27),Card=Color.FromArgb(27,31,36),
        Border=Color.FromArgb(42,47,54),Text=Color.FromArgb(231,237,241),Muted=Color.FromArgb(143,155,167),
        Accent=Color.FromArgb(36,194,169),Warning=Color.FromArgb(240,179,63),Hot=Color.FromArgb(255,95,112);
    public static Color Load(double? value)=>value>=90?Hot:value>=75?Warning:Accent;
    public static ContextMenuStrip Menu()=>new(){BackColor=Card,ForeColor=Text,ShowImageMargin=false,ShowCheckMargin=true,
        Renderer=new MenuRenderer()};
    sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer():base(new MenuColors()){RoundedEdges=false;}
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var state=e.Graphics.Save();
            e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var box=e.ImageRectangle;
            float size=Math.Min(box.Width,box.Height)*0.64f;
            float x=box.Left+(box.Width-size)/2f,y=box.Top+(box.Height-size)/2f;
            using var pen=new Pen(Accent,Math.Max(1.5f,(e.ToolStrip?.DeviceDpi??96)/96f*1.6f)){
                StartCap=System.Drawing.Drawing2D.LineCap.Round,EndCap=System.Drawing.Drawing2D.LineCap.Round,
                LineJoin=System.Drawing.Drawing2D.LineJoin.Round};
            e.Graphics.DrawLines(pen,[new PointF(x,y+size*0.5f),new PointF(x+size*0.35f,y+size*0.85f),new PointF(x+size,y+size*0.12f)]);
            e.Graphics.Restore(state);
        }
    }
    sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground=>Card;
        public override Color MenuBorder=>Border;
        public override Color MenuItemBorder=>Accent;
        public override Color MenuItemSelected=>Color.FromArgb(44,55,61);
        public override Color MenuItemSelectedGradientBegin=>MenuItemSelected;
        public override Color MenuItemSelectedGradientEnd=>MenuItemSelected;
        public override Color CheckBackground=>Card;
        public override Color CheckSelectedBackground=>MenuItemSelected;
        public override Color CheckPressedBackground=>MenuItemSelected;
        public override Color ImageMarginGradientBegin=>Card;
        public override Color ImageMarginGradientMiddle=>Card;
        public override Color ImageMarginGradientEnd=>Card;
    }
    public static Button Button(string text,EventHandler clicked)
    {
        var b=new Button{Text=text,AutoSize=true,FlatStyle=FlatStyle.Flat,BackColor=Card,ForeColor=Text,Padding=new Padding(7,3,7,3),Cursor=Cursors.Hand};
        b.FlatAppearance.BorderColor=Border;b.Click+=clicked;return b;
    }
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
    public static Icon Icon()
    {
        using var bitmap=new Bitmap(32,32);using var g=Graphics.FromImage(bitmap);
        using var brush=new SolidBrush(Accent);
        g.FillRectangle(brush,4,17,4,10);g.FillRectangle(brush,11,8,4,19);g.FillRectangle(brush,18,13,4,14);g.FillRectangle(brush,25,3,4,24);
        var handle=bitmap.GetHicon();
        try{using var temporary=System.Drawing.Icon.FromHandle(handle);return (Icon)temporary.Clone();}
        finally{DestroyIcon(handle);}
    }
}
internal sealed class PanelCloseButton : Button
{
    public PanelCloseButton()
    {
        AccessibleName="Close panel";AccessibleRole=AccessibleRole.PushButton;
        AutoSize=false;FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;
        FlatAppearance.MouseOverBackColor=Theme.Card;FlatAppearance.MouseDownBackColor=Theme.Border;
        BackColor=Theme.Background;ForeColor=Theme.Muted;Cursor=Cursors.Hand;Padding=Padding.Empty;
        SetStyle(ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var state=e.Graphics.Save();e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float half=5f*DeviceDpi/96f,cx=(ClientSize.Width-1)/2f,cy=(ClientSize.Height-1)/2f;
        using var pen=new Pen(Focused||ClientRectangle.Contains(PointToClient(Cursor.Position))?Theme.Text:ForeColor,1.6f*DeviceDpi/96f){
            StartCap=System.Drawing.Drawing2D.LineCap.Round,EndCap=System.Drawing.Drawing2D.LineCap.Round};
        e.Graphics.DrawLine(pen,cx-half,cy-half,cx+half,cy+half);
        e.Graphics.DrawLine(pen,cx+half,cy-half,cx-half,cy+half);
        e.Graphics.Restore(state);
    }
}
