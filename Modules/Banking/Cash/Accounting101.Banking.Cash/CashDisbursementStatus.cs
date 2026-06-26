using System.Text.Json.Serialization;

namespace Accounting101.Banking.Cash;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CashDisbursementStatus { Posted, Void }
