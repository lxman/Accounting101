using System.ComponentModel;
using Accounting101.Angular.DataAccess.Interfaces;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services.Interfaces;
using Accounting101.Angular.Server.Services;
using ModelContextProtocol.Server;

namespace Accounting101.Angular.Server.MCP;

[McpServerToolType]
public class AccountingTools
{
    private readonly IClientService _clientService;
    private readonly IBusinessService _businessService;
    private readonly IDataStore _dataStore;

    public AccountingTools(
        IClientService clientService,
        IBusinessService businessService,
        IDataStore dataStore)
    {
        _clientService = clientService;
        _businessService = businessService;
        _dataStore = dataStore;
    }

    [McpServerTool, Description("Get business information")]
    public async Task<string> GetBusinessInfoAsync(
        [Description("The business database ID")] string businessId,
        CancellationToken cancellationToken)
    {
        var business = await _dataStore.GetBusinessAsync(businessId);

        if (business == null)
        {
            return $"No business found with ID {businessId}.";
        }

        string addressInfo = business.Address != null
            ? $"Address: {FormatAddress(business.Address)}"
            : "No address information available";

        return $"Business: {business.Name}\n" +
               $"ID: {business.Id}\n" +
               addressInfo;
    }

    private string FormatAddress(IAddress address)
    {
        if (address is UsAddress usAddress)
        {
            return $"{usAddress.Line1}, {usAddress.City}, {usAddress.State} {usAddress.Zip}";
        }
        else if (address is ForeignAddress foreignAddress)
        {
            return $"{foreignAddress.Line1}, {foreignAddress.PostalCode}, {foreignAddress.Province}, {foreignAddress.Country}";
        }

        return "Unknown address format";
    }

    [McpServerTool, Description("Get a list of states for US addresses")]
    public async Task<string> GetStatesAsync(CancellationToken cancellationToken)
    {
        var states = await _dataStore.GetStatesAsync();
        
        if (states == null || !states.Any())
        {
            return "No states found.";
        }

        return $"Available states:\n" + 
               string.Join("\n", states);
    }
    
    [McpServerTool, Description("Get a list of countries for foreign addresses")]
    public async Task<string> GetCountriesAsync(CancellationToken cancellationToken)
    {
        var countries = await _dataStore.GetCountriesAsync();
        
        if (countries == null || !countries.Any())
        {
            return "No countries found.";
        }

        return $"Available countries:\n" + 
               string.Join("\n", countries);
    }
    
    [McpServerTool, Description("Create a new business")]
    public async Task<string> CreateBusinessAsync(
        [Description("The business database ID")] string businessId,
        [Description("The business name")] string name,
        [Description("Is the address foreign (true) or US (false)")] bool isForeign,
        CancellationToken cancellationToken)
    {
        try
        {
            // Check if business already exists
            if (await _dataStore.GetBusinessAsync(businessId) != null)
            {
                return $"A business with ID {businessId} already exists.";
            }
            
            var business = new Business { Name = name };
            
            // Add a dummy address based on type
            if (isForeign)
            {
                business.Address = new ForeignAddress
                {
                    Country = "Sample Country",
                    Line1 = "123 Sample St",
                    PostalCode = "12345",
                    Province = "Sample Province"
                };
            }
            else
            {
                business.Address = new UsAddress
                {
                    City = "Sample City",
                    State = "Sample State",
                    Zip = "12345",
                    Line1 = "123 Sample St"
                };
            }
            
            bool success = await _dataStore.CreateBusinessAsync(businessId, business);
            
            return success
                ? $"Successfully created business '{name}' with ID {businessId}"
                : $"Failed to create business with ID {businessId}";
        }
        catch (Exception ex)
        {
            return $"Error creating business: {ex.Message}";
        }
    }
}