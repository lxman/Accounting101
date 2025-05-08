# Model Context Protocol (MCP) Implementation

This document provides an overview of the Model Context Protocol (MCP) implementation in the Accounting101 application.

## Overview

The Model Context Protocol (MCP) is an open protocol that standardizes how applications provide context to Large Language Models (LLMs). It enables secure integration between LLMs and various data sources and tools.

In the Accounting101 application, MCP is implemented to allow LLMs to interact with accounting data and functions through standardized tools and prompts.

## Implementation Components

1. **AccountingTools.cs** - Contains tool implementations that can be invoked by the LLM:
   - `GetBusinesses` - Retrieves a list of all businesses
   - `GetClientsForBusiness` - Retrieves clients for a specific business
   - `GetAccountInfo` - Retrieves account information and current balance
   - `CreateSampleTransaction` - Creates a sample transaction (mock implementation)

2. **AccountingPrompts.cs** - Contains predefined prompts that can be used by the LLM:
   - `AnalyzeTransactions` - Creates a prompt to analyze financial transactions
   - `GenerateFinancialReport` - Creates a prompt to generate a financial report
   - `ExplainAccountingConcept` - Creates a prompt to explain an accounting concept

3. **McpHttpHandler.cs** - Handles HTTP requests to the MCP endpoint

4. **McpConfiguration.cs** - Contains configuration methods for registering and using the MCP server

## Endpoint

The MCP server is accessible at:

```
POST /mcp
```

## Usage

LLM clients can connect to this MCP server endpoint to:

1. List available tools and prompts
2. Invoke tools to retrieve accounting data
3. Use predefined prompts as templates for financial analysis and reporting

## Example Request

```json
{
  "jsonrpc": "2.0",
  "id": "1",
  "method": "call_tool",
  "params": {
    "name": "GetBusinesses"
  }
}
```

## Example Response

```json
{
  "jsonrpc": "2.0",
  "id": "1",
  "result": {
    "content": [
      {
        "type": "text",
        "text": "Found 3 businesses:\n- Acme Corp (ID: 12345)\n- Global Industries (ID: 67890)\n- Local Shop (ID: 24680)"
      }
    ]
  }
}
```

## Future Enhancements

1. Add more accounting-specific tools
2. Implement authentication for the MCP endpoint
3. Add support for handling file uploads and downloads
4. Integrate with reporting and visualization capabilities
