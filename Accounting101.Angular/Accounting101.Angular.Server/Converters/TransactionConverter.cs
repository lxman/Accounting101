using System.Text.Json;
using System.Text.Json.Serialization;
using Accounting101.Angular.DataAccess.Models;

namespace Accounting101.Angular.Server.Converters;

public class TransactionConverter : JsonConverter<Transaction>
{
    public override Transaction? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (typeToConvert != typeof(Transaction))
        {
            return null;
        }
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        string creditedAccountId = document.RootElement.GetProperty("creditedAccountId").GetString() ?? string.Empty;
        string debitedAccountId = document.RootElement.GetProperty("debitedAccountId").GetString() ?? string.Empty;
        string amountSource = document.RootElement.GetProperty("amount").GetString() ?? string.Empty;
        decimal amount = decimal.Parse(amountSource);
        DateOnly when = DateOnly.Parse(document.RootElement.GetProperty("when").GetString() ?? string.Empty);
        Guid id = document.RootElement.GetProperty("id").GetGuid();

        return new Transaction(id, creditedAccountId, debitedAccountId, amount, when);
    }

    public override void Write(Utf8JsonWriter writer, Transaction value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("creditedAccountId");
        writer.WriteStringValue(value.CreditedAccountId);
        writer.WritePropertyName("debitedAccountId");
        writer.WriteStringValue(value.DebitedAccountId);
        writer.WritePropertyName("amount");
        writer.WriteNumberValue(value.Amount);
        writer.WritePropertyName("when");
        writer.WriteStringValue(value.When.ToString("yyyy-MM-dd"));
        writer.WritePropertyName("id");
        writer.WriteStringValue(value.Id.ToString());
        writer.WriteEndObject();
    }
}
