using System.Text.Json;
using System.Text.Json.Serialization;
namespace Tailwatch.Core;

public record Reading(double? Value = null, string Status = "unsupported", long AgeMs = 0)
{
    [JsonIgnore] public bool IsFresh => Status == "ok" && AgeMs is >= 0 and <= 10000 && Value is double v && Protocol.ValidPercentage(v);
}
public record CpuReading : Reading
{
    public string Model { get; init; } = "";
    public int LogicalCores { get; init; }
}
public record MemoryReading(ulong? UsedBytes = null, ulong? TotalBytes = null, string Status = "unsupported", long AgeMs = 0, string Source = "")
{
    [JsonIgnore] public double? Percentage => Status == "ok" && AgeMs is >= 0 and <= 10000 && TotalBytes > 0 && UsedBytes <= TotalBytes ? 100.0 * UsedBytes / TotalBytes : null;
}
public record GpuReading(string Id, string Name, string Vendor, Reading Utilization, string Metric, MemoryReading? Memory = null);
public record Snapshot
{
    public int SchemaVersion { get; init; } = 1;
    public string AgentVersion { get; init; } = "";
    public string NodeId { get; init; } = "";
    public string Hostname { get; init; } = "";
    public string Os { get; init; } = "";
    public string BootId { get; init; } = "";
    public ulong Sequence { get; init; }
    public long IntervalMs { get; init; }
    public long AgeMs { get; init; }
    public CpuReading Cpu { get; init; } = new();
    public MemoryReading Ram { get; init; } = new();
    public List<GpuReading> Gpus { get; init; } = [];
    public string GpuStatus { get; init; } = "unsupported";
    [JsonIgnore] public double? GpuPercent => Gpus.Where(g=>g.Utilization.IsFresh).Select(g=>g.Utilization.Value).DefaultIfEmpty(null).Max();
    [JsonIgnore] public bool PartialGpu => GpuPercent != null && (GpuStatus == "partial" || Gpus.Any(g=>!g.Utilization.IsFresh));
}
public class ProtocolException(string message,bool disconnected=false) : Exception(message)
{
    public bool Disconnected {get;}=disconnected;
}
public static class Protocol
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true, WriteIndented = true, MaxDepth = 32 };
    public static bool ValidPercentage(double value) => double.IsFinite(value) && value is >= 0 and <= 100;
    public static Snapshot Parse(ReadOnlySpan<byte> bytes, string expectedNodeId)
    {
        if(bytes.Length>65536) throw new ProtocolException("Response too large");
        Snapshot value;
        try { value = JsonSerializer.Deserialize<Snapshot>(bytes, Json) ?? throw new ProtocolException("Empty response"); }
        catch(JsonException) { throw new ProtocolException("Invalid agent response"); }
        if(value.SchemaVersion != 1) throw new ProtocolException("Incompatible agent version");
        if(string.IsNullOrEmpty(expectedNodeId) || value.NodeId != expectedNodeId) throw new ProtocolException("Agent identity mismatch");
        if(string.IsNullOrEmpty(value.BootId) || value.AgeMs < 0 || value.IntervalMs is < 100 or > 60000 ||
            value.Cpu == null || value.Ram == null || value.Gpus == null || value.Gpus.Count>64) throw new ProtocolException("Incomplete agent response");
        Validate(value.Cpu);
        Validate(value.Ram);
        var ids=new HashSet<string>();
        foreach(var gpu in value.Gpus)
        {
            if(gpu == null || string.IsNullOrEmpty(gpu.Id) || !ids.Add(gpu.Id) || gpu.Utilization == null) throw new ProtocolException("Invalid GPU");
            Validate(gpu.Utilization);
            if(gpu.Memory != null) Validate(gpu.Memory);
        }
        return value;
    }
    static void Validate(Reading r)
    {
        if(r.AgeMs<0 || string.IsNullOrEmpty(r.Status) || (r.Value.HasValue && !ValidPercentage(r.Value.Value)) ||
            (r.Status=="ok" && !r.Value.HasValue)) throw new ProtocolException("Invalid utilization");
    }
    static void Validate(MemoryReading r)
    {
        if(r.AgeMs<0 || string.IsNullOrEmpty(r.Status) || (r.UsedBytes.HasValue && r.TotalBytes.HasValue && r.UsedBytes>r.TotalBytes) ||
            (r.Status=="ok" && (r.TotalBytes is null or 0 || r.UsedBytes==null))) throw new ProtocolException("Invalid memory reading");
    }
}

