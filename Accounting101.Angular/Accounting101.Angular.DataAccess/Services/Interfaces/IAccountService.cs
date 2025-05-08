using System;
using System.Threading;
using System.Threading.Tasks;
using Accounting101.Angular.DataAccess.Models;

namespace Accounting101.Angular.DataAccess.Services.Interfaces;

public interface IAccountService
{
    Task<Account?> GetAccountAsync(string accountId, CancellationToken cancellationToken = default);
    Task<decimal> GetAccountBalanceAsync(string accountId, DateOnly asOfDate, CancellationToken cancellationToken = default);
    // Additional methods would be defined here in a real implementation
}
