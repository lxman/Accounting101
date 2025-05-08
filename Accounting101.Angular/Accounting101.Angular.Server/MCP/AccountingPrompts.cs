using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace Accounting101.Angular.Server.MCP;

[McpServerPromptType]
public static class AccountingPrompts
{
    [McpServerPrompt, Description("Creates a prompt to analyze financial transactions")]
    public static ChatMessage AnalyzeTransactions(
        [Description("The raw transaction data to analyze")] string transactionData) =>
        new(ChatRole.User, $"""
            You are a financial analyst. Please analyze the following transaction data and provide insights:
            
            {transactionData}
            
            Please include:
            1. Total number of transactions
            2. Total money movement
            3. Largest transaction
            4. Any unusual patterns or potential issues
            5. A summary of the overall financial activity
            """);
    
    [McpServerPrompt, Description("Creates a prompt to generate a financial report")]
    public static ChatMessage GenerateFinancialReport(
        [Description("The business name")] string businessName,
        [Description("The time period for the report")] string timePeriod,
        [Description("The financial data to include in the report")] string financialData) =>
        new(ChatRole.User, $"""
            As a financial expert, create a professional financial report for {businessName} covering the {timePeriod}.
            
            Use the following data to prepare the report:
            
            {financialData}
            
            The report should include:
            - Executive Summary
            - Income Statement
            - Balance Sheet Analysis
            - Cash Flow Statement
            - Key Financial Ratios
            - Recommendations for Improvement
            
            Format the report professionally with clear sections and appropriate financial terminology.
            """);
    
    [McpServerPrompt, Description("Creates a prompt to explain an accounting concept")]
    public static ChatMessage ExplainAccountingConcept(
        [Description("The accounting concept to explain")] string concept) =>
        new(ChatRole.User, $"""
            Explain the accounting concept of "{concept}" in detail.
            
            Include:
            1. Definition and core principles
            2. How it's applied in practice
            3. Examples that illustrate the concept
            4. Common misconceptions
            5. Why it's important in accounting
            
            Explain in a way that would be clear to someone with limited accounting knowledge.
            """);
}
