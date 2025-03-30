using System.Text.Json;
using System.Text.Json.Nodes;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services.Interfaces;

namespace Accounting101.Angular.Server.Services
{
    public interface IBusinessService
    {
        Task<bool> CreateBusinessAsync(Guid dbId, Stream body, IDataStore dataStore);
    }

    public class BusinessService : IBusinessService
    {
        public async Task<bool> CreateBusinessAsync(Guid dbId, Stream body, IDataStore dataStore)
        {
            if (await dataStore.GetBusinessAsync(dbId.ToString()) is not null)
            {
                return false;
            }
            var jsonNode = await JsonSerializer.DeserializeAsync<JsonNode>(body);
            JsonNode? address = jsonNode?["address"];
            if (address is null)
            {
                return false;
            }

            var business = new Business { Name = jsonNode?["name"]?.GetValue<string>() ?? string.Empty };
            bool isForeign = address["isForeign"]?.GetValue<bool>() ?? false;
            if (isForeign)
            {
                business.Address = new ForeignAddress
                {
                    Country = address["country"]?.GetValue<string>() ?? string.Empty,
                    Line1 = address["line1"]?.GetValue<string>() ?? string.Empty,
                    Line2 = address["line2"]?.GetValue<string>() ?? string.Empty,
                    PostalCode = address["postCode"]?.GetValue<string>() ?? string.Empty,
                    Province = address["province"]?.GetValue<string>() ?? string.Empty
                };
            }
            else
            {
                business.Address = new UsAddress
                {
                    City = address["city"]?.GetValue<string>() ?? string.Empty,
                    State = address["state"]?.GetValue<string>() ?? string.Empty,
                    Zip = address["zip"]?.GetValue<string>() ?? string.Empty,
                    Line1 = address["line1"]?.GetValue<string>() ?? string.Empty,
                    Line2 = address["line2"]?.GetValue<string>() ?? string.Empty
                };
            }
            return await dataStore.CreateBusinessAsync(dbId.ToString(), business);
        }
    }
}
