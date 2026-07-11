using System.Text.Json;
using Accounting101.ModuleKit;
using Xunit;

namespace Accounting101.ModuleKit.Tests;

public class AccountReadinessStatusSerializationTests
{
    [Fact]
    public void Status_serializes_as_a_string_not_a_number()
    {
        var report = new ChartReadinessReport(
            "payroll",
            false,
            [new AccountReadinessResult(
                Guid.Empty, "Withholdings Payable", "Liability", [],
                AccountReadinessStatus.Missing, null, null, "add a Liability account")]);

        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"status\":\"Missing\"", json);
        Assert.DoesNotContain("\"status\":1", json);
    }
}
