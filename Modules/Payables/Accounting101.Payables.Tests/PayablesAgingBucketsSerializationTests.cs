using System.Text.Json;

namespace Accounting101.Payables.Tests;

/// <summary>Wire-contract guard: pins the exact JSON keys the UI AgingBuckets interface must mirror.
/// Interior capitals are preserved by camelCase: d1To30, NOT d1to30.</summary>
public sealed class PayablesAgingBucketsSerializationTests
{
    [Fact]
    public void AgingBuckets_serializes_with_camelCase_wire_keys()
    {
        AgingBuckets buckets = new(1m, 2m, 3m, 4m, 5m);
        string json = JsonSerializer.Serialize(buckets, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"current\"", json);
        Assert.Contains("\"d1To30\"", json);
        Assert.Contains("\"d31To60\"", json);
        Assert.Contains("\"d61To90\"", json);
        Assert.Contains("\"d90Plus\"", json);
    }
}
