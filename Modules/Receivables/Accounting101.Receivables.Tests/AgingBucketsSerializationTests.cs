using System.Text.Json;
using Accounting101.Receivables;

namespace Accounting101.Receivables.Tests;

/// <summary>Wire-contract guard: pins the exact JSON keys that the UI must mirror.
/// Catches any future rename of AgingBuckets positional members that would silently break the aging panel.</summary>
public sealed class AgingBucketsSerializationTests
{
    [Fact]
    public void AgingBuckets_serializes_with_camelCase_wire_keys_matching_the_UI_interface()
    {
        AgingBuckets buckets = new(1m, 2m, 3m, 4m, 5m);
        string json = JsonSerializer.Serialize(buckets, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        // These are the exact keys the Angular AgingBuckets interface must declare.
        // JsonSerializerDefaults.Web = camelCase, matching the host's JsonNamingPolicy.CamelCase.
        // Interior capitals are preserved: D1To30, not d1to30.
        Assert.Contains("\"current\"", json);
        Assert.Contains("\"d1To30\"", json);
        Assert.Contains("\"d31To60\"", json);
        Assert.Contains("\"d61To90\"", json);
        Assert.Contains("\"d90Plus\"", json);
    }
}
