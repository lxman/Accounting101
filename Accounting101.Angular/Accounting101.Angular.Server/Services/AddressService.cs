using System.Text.Json;
using System.Text.Json.Nodes;
using Accounting101.Angular.DataAccess;
using Accounting101.Angular.DataAccess.Interfaces;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services.Interfaces;

namespace Accounting101.Angular.Server.Services;

public interface IAddressService
{
    Task<Guid?> CreateAddressAsync(Guid dbId, Stream body, IDataStore dataStore);
}

public class AddressService : IAddressService
{
    public async Task<Guid?> CreateAddressAsync(Guid dbId, Stream body, IDataStore dataStore)
    {
        var jsonNode = await JsonSerializer.DeserializeAsync<JsonNode>(body);
        if (jsonNode is null)
        {
            return null;
        }
        bool isForeign = jsonNode["isForeign"]?.GetValue<bool>() ?? false;
        IAddress resolved;
        if (isForeign)
        {
            resolved = new ForeignAddress
            {
                Line1 = jsonNode["line1"]?.GetValue<string>() ?? string.Empty,
                Line2 = jsonNode["line2"]?.GetValue<string>() ?? string.Empty,
                Province = jsonNode["stateProvince"]?.GetValue<string>() ?? string.Empty,
                PostalCode = jsonNode["postalCode"]?.GetValue<string>() ?? string.Empty,
                Country = jsonNode["country"]?.GetValue<string>() ?? string.Empty
            };
        }
        else
        {
            resolved = new UsAddress
            {
                Line1 = jsonNode["line1"]?.GetValue<string>() ?? string.Empty,
                Line2 = jsonNode["line2"]?.GetValue<string>() ?? string.Empty,
                City = jsonNode["city"]?.GetValue<string>() ?? string.Empty,
                State = jsonNode["stateProvince"]?.GetValue<string>() ?? string.Empty,
                Zip = jsonNode["postalCode"]?.GetValue<string>() ?? string.Empty,
                Country = "US"
            };
        }
        return await dataStore.CreateAddressAsync(dbId.ToString(), resolved);
    }
}
