namespace Tailwatch.App;
internal static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex=new Mutex(true,@"Local\Tailwatch.Tray",out bool first);
        using var show=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\Tailwatch.Show");
        if(!first){show.Set();return;}
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException+=(_,e)=>Log.Write(e.Exception.Message);
        using var context=new TrayContext(show);
        Application.Run(context);
    }
}
internal static class Log
{
    static readonly object Sync=new();
    public static readonly string Folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Tailwatch");
    public static void Write(string message)
    {
        lock(Sync)try
        {
            Directory.CreateDirectory(Folder);var path=Path.Combine(Folder,"tailwatch.log");
            if(File.Exists(path)&&new FileInfo(path).Length>262144)File.Move(path,path+".previous",true);
            var bounded=message.Length>2048?message[..2048]+"…":message;
            File.AppendAllText(path,DateTimeOffset.Now.ToString("O")+" "+bounded.Replace('\n',' ').Replace('\r',' ')+Environment.NewLine);
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){}
    }
}
