using System.Text.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class ApprovalPolicyTests
{
    [Fact]
    public void Stored_mode_wins_over_legacy_bool()
    {
        ClientRegistration c = new() { ApprovalMode = ApprovalMode.AutoApprove, RequireSegregationOfDuties = true };
        Assert.Equal(ApprovalMode.AutoApprove, ApprovalPolicy.ModeOf(c));
    }

    [Fact]
    public void Legacy_true_normalizes_to_two_person()
    {
        ClientRegistration c = new() { ApprovalMode = ApprovalMode.Unspecified, RequireSegregationOfDuties = true };
        Assert.Equal(ApprovalMode.TwoPerson, ApprovalPolicy.ModeOf(c));
    }

    [Fact]
    public void Legacy_false_normalizes_to_self_approve()
    {
        ClientRegistration c = new() { ApprovalMode = ApprovalMode.Unspecified, RequireSegregationOfDuties = false };
        Assert.Equal(ApprovalMode.SelfApprove, ApprovalPolicy.ModeOf(c));
    }

    [Fact]
    public void Mode_serializes_as_a_string_not_a_number()
    {
        string json = JsonSerializer.Serialize(new { Mode = ApprovalMode.AutoApprove });
        Assert.Contains("\"AutoApprove\"", json);
        Assert.DoesNotContain("3", json);
    }
}
