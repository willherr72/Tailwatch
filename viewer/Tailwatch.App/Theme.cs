using System.Runtime.InteropServices;
namespace Tailwatch.App;
internal static class Theme
{
    public static readonly Color Background=Color.FromArgb(20,23,27),Card=Color.FromArgb(27,31,36),
        Border=Color.FromArgb(42,47,54),Text=Color.FromArgb(231,237,241),Muted=Color.FromArgb(143,155,167),
        Accent=Color.FromArgb(36,194,169),Warning=Color.FromArgb(240,179,63),Hot=Color.FromArgb(255,95,112);
    public static Color Load(double? value)=>value>=90?Hot:value>=75?Warning:Accent;
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
