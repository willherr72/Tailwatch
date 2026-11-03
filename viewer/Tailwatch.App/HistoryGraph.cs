using System.Drawing.Drawing2D;
using Tailwatch.Core;
namespace Tailwatch.App;

internal sealed class HistoryGraph : Control
{
    IReadOnlyList<HistoryPoint> points=[];
    int S(int value)=>(int)Math.Round(value*DeviceDpi/96f);
    public HistoryGraph()
    {
        DoubleBuffered=true;ResizeRedraw=true;BackColor=Theme.Card;ForeColor=Theme.Muted;
        AccessibleName="CPU, GPU and RAM history";AccessibleRole=AccessibleRole.Chart;
        Font=new Font("Segoe UI",8f);TabStop=false;
    }
    public void UpdateHistory(IReadOnlyList<HistoryPoint>? values)
    {
        points=values??[];
        AccessibleDescription="Last 15 minutes of this session. Missing readings are gaps. "+string.Join("; ",new[]{"CPU","GPU","RAM"}.Select((name,index)=>{
            var readings=points.Select(p=>Value(p,index)).Where(v=>v.HasValue).Select(v=>v!.Value).ToList();
            return readings.Count==0?name+": waiting for readings":$"{name}: {readings.Count} readings, {readings.Min():0} to {readings.Max():0} percent";
        }));
        Invalidate();
    }
    static double? Value(HistoryPoint point,int metric)=>metric switch{0=>point.Cpu,1=>point.Gpu,_=>point.Ram};
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        TextRenderer.DrawText(e.Graphics,"LAST 15 MINUTES · SESSION ONLY",Font,new Rectangle(S(8),S(5),Width-S(16),S(18)),Theme.Muted,TextFormatFlags.Left|TextFormatFlags.NoPrefix);
        var names=new[]{"CPU","GPU","RAM"};var colors=new[]{Theme.Accent,Color.FromArgb(178,148,255),Color.FromArgb(98,177,255)};
        int column=Math.Max(1,(Width-S(16))/3);
        for(int metric=0;metric<3;metric++)
        {
            int left=S(8)+metric*column;
            TextRenderer.DrawText(e.Graphics,names[metric],Font,new Rectangle(left,S(25),column-S(10),S(17)),colors[metric]);
            var plot=new RectangleF(left,S(46),Math.Max(1,column-S(12)),S(50));
            using var grid=new Pen(Theme.Border);
            e.Graphics.DrawLine(grid,plot.Left,plot.Top,plot.Right,plot.Top);
            e.Graphics.DrawLine(grid,plot.Left,plot.Bottom,plot.Right,plot.Bottom);
            using var pen=new Pen(colors[metric],Math.Max(1.3f,S(1)));
            using var dot=new SolidBrush(colors[metric]);
            PointF? previous=null;TimeSpan previousAge=default;bool any=false;
            foreach(var point in points)
            {
                var value=Value(point,metric);
                if(value==null||point.Age<TimeSpan.Zero||point.Age>MetricHistory.Window){previous=null;continue;}
                var location=new PointF(plot.Right-(float)(point.Age.TotalSeconds/MetricHistory.Window.TotalSeconds)*plot.Width,
                    plot.Bottom-(float)Math.Clamp(value.Value/100,0,1)*plot.Height);
                if(previous is PointF from&&previousAge-point.Age<=TimeSpan.FromSeconds(20))e.Graphics.DrawLine(pen,from,location);
                e.Graphics.FillEllipse(dot,location.X-S(1),location.Y-S(1),S(2),S(2));
                previous=location;previousAge=point.Age;any=true;
            }
            if(!any)TextRenderer.DrawText(e.Graphics,"Collecting…",Font,Rectangle.Round(plot),Theme.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
        }
        TextRenderer.DrawText(e.Graphics,"15m ago",Font,new Rectangle(S(8),S(101),Width/2,S(17)),Theme.Muted,TextFormatFlags.Left);
        TextRenderer.DrawText(e.Graphics,"0–100%  ·  now",Font,new Rectangle(Width/2,S(101),Width/2-S(8),S(17)),Theme.Muted,TextFormatFlags.Right);
    }
    protected override void Dispose(bool disposing){if(disposing)Font.Dispose();base.Dispose(disposing);}
}
