using System.Text.Json.Serialization;

namespace Accounting101.Ledger.Contracts;

/// <summary>
/// A client's approval posture (host policy, per client). One enum so the illegal combination —
/// segregation of duties on AND auto-approve on — is unrepresentable. <see cref="Unspecified"/> is a
/// legacy sentinel: a document stored before this field existed deserializes to it, and readers
/// normalize via <c>ApprovalPolicy.ModeOf</c>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApprovalMode
{
    Unspecified = 0,
    TwoPerson = 1,
    SelfApprove = 2,
    AutoApprove = 3,
}
