using System.Text.Json.Serialization;

namespace Accounting101.Payroll;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaxRemittanceStatus { Posted, Void }
