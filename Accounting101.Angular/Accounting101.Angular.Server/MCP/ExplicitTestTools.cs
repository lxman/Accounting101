using System.ComponentModel;
using System.Threading.Tasks;
using System.Threading;
using ModelContextProtocol.Server;
using Accounting101.Angular.DataAccess.Services.Interfaces;
using System.Linq;
using System;

namespace Accounting101.Angular.Server.MCP;

/// <summary>
/// A simplified test class that explicitly uses the exact attribute class from 
/// the ModelContextProtocol.Server namespace.
/// </summary>
[McpServerToolType]
public class ExplicitTestTools
{
    private readonly IDataStore _dataStore;
    
    public ExplicitTestTools(IDataStore dataStore)
    {
        _dataStore = dataStore;
    }

    /// <summary>
    /// Test echo method
    /// </summary>
    /// <param name="message">The message to echo</param>
    /// <returns>The echoed message</returns>
    [ModelContextProtocol.Server.McpServerTool]  // Explicitly using fully qualified name
    [Description("Echoes the message back to the client.")]
    public string Echo(string message)
    {
        Console.WriteLine($"Echo method called with: {message}");
        return $"Echo: {message}";
    }
    
    /// <summary>
    /// Simple test method with no parameters
    /// </summary>
    /// <returns>A test message</returns>
    [ModelContextProtocol.Server.McpServerTool]  // Explicitly using fully qualified name
    [Description("A simple test method")]
    public string Test()
    {
        Console.WriteLine("Test method called");
        return "Test method works!";
    }
    
    /// <summary>
    /// Get a list of US states
    /// </summary>
    /// <returns>List of states</returns>
    [ModelContextProtocol.Server.McpServerTool]  // Explicitly using fully qualified name
    [Description("Get a list of states for US addresses")]
    public async Task<string> GetStates(CancellationToken cancellationToken)
    {
        Console.WriteLine("GetStates called");
        
        var states = await _dataStore.GetStatesAsync();
        
        if (states == null || !states.Any())
        {
            return "No states found.";
        }
        
        return $"Available states:\n{string.Join("\n", states)}";
    }
}