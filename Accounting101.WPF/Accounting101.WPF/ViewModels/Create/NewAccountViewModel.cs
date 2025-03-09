using System.Collections.ObjectModel;
using Accounting101.WPF.Messages;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess.WPF;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

#pragma warning disable CS8618, CS9264

namespace Accounting101.WPF.ViewModels.Create;

public class NewAccountViewModel : BaseViewModel
{
    public ReadOnlyObservableCollection<BaseAccountTypes> AccountTypes { get; private set; }
        = new([
            BaseAccountTypes.Asset,
            BaseAccountTypes.Liability,
            BaseAccountTypes.Equity,
            BaseAccountTypes.Revenue,
            BaseAccountTypes.Expense
        ]);

    public string Name { get; set; } = string.Empty;

    public BaseAccountTypes Type { get; set; }

    public string CoAId { get; set; } = string.Empty;

    public decimal StartBalance { get; set; }

    public DateOnly Created { get; set; } = DateOnly.FromDateTime(DateTime.Today);

    private IDataStore _dataStore;
    private JoinableTaskFactory _taskFactory;
    private Client _client;

    public void SetInfo(IDataStore dataStore, JoinableTaskFactory taskFactory, Client client)
    {
        _dataStore = dataStore;
        _taskFactory = taskFactory;
        _client = client;
    }

    public void Save()
    {
        AccountInfo info = new()
        {
            Name = Name,
            CoAId = CoAId
        };
        Account acct = new()
        {
            Type = Type,
            ClientId = _client.Id,
            StartBalance = StartBalance,
            Created = Created
        };
        Guid result = _taskFactory.Run(() => _dataStore.CreateAccountAsync(acct, info));
        if (result != Guid.Empty)
        {
            WeakReferenceMessenger.Default.Send(new ChangeScreenMessage(WindowType.ClientAccountList));
        }

        // TODO: Add error handling
    }
}