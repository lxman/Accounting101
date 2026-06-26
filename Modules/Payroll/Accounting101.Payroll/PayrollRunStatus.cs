using System.Text.Json.Serialization;

namespace Accounting101.Payroll;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PayrollRunStatus { Posted, Void }
