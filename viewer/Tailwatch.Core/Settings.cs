using System.Text.Json;
namespace Tailwatch.Core;
public class ProfileSettings
{
    public Dictionary<string,bool> Monitored {get;set;}=[];
    public Dictionary<string,int> Ports {get;set;}=[];
    public string Sort {get;set;}="Name";
    public bool ReportingOnly {get;set;}
}
public class AppSettings
{
    public Dictionary<string,ProfileSettings> Profiles {get;set;}=[];
    public ProfileSettings For(string key)
    {
        if(!Profiles.TryGetValue(key,out var settings)) Profiles[key]=settings=new();
        return settings;
    }
    public static AppSettings Load(string path)
    {
        try
        {
            if(!File.Exists(path)) return new();
            var value=JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path),Protocol.Json)??new();
            if(value.Profiles==null || value.Profiles.Any(p=>p.Value==null || p.Value.Monitored==null || p.Value.Ports==null)) return new();
            return value;
        }
        catch(Exception e) when(e is IOException or JsonException or UnauthorizedAccessException){return new();}
    }
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary=path+".tmp";
        File.WriteAllText(temporary,JsonSerializer.Serialize(this,Protocol.Json));
        File.Move(temporary,path,true);
    }
}

