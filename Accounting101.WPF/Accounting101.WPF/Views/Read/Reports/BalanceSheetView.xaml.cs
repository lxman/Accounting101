using CommunityToolkit.Mvvm.ComponentModel;
using DataAccess.WPF;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

#pragma warning disable CS8618, CS9264

namespace Accounting101.WPF.Views.Read.Reports;

[ObservableObject]
public partial class BalanceSheetView
{
    public DateOnly Date
    {
        get => _date;
        set
        {
            if (value == _date)
            {
                return;
            }
            _date = value;
            OnPropertyChanged();
            Recalculate();
        }
    }

    public string Balanced
    {
        get => _balanced;
        set
        {
            if (value == _balanced)
            {
                return;
            }
            _balanced = value;
            OnPropertyChanged();
        }
    }

    private string _balanced;
    private DateOnly _date;
    private readonly DateOnly _startDate;
    private readonly IDataStore _dataStore;
    private readonly JoinableTaskFactory _taskFactory;
    private readonly List<AccountWithInfo> _assets;
    private readonly List<AccountWithInfo> _liabilities;
    private readonly List<AccountWithInfo> _equity;

    public BalanceSheetView(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid clientId)
    {
        _dataStore = dataStore;
        _taskFactory = taskFactory;
        DataContext = this;
        InitializeComponent();
        ClientWithInfo client = taskFactory.Run(() => dataStore.GetClientWithInfoAsync(clientId)) ?? new ClientWithInfo();
        CheckPoint? checkPoint = taskFactory.Run(() => dataStore.GetCheckpointAsync(clientId));
        ClientHeader.SetInfo(client, checkPoint);
        List<AccountWithInfo> all = taskFactory.Run(() => dataStore.AccountsForClientAsync(clientId))?.ToList() ?? [];
        _startDate = all.Min(a => a.Created);
        _assets = all.Where(a => a.Type == BaseAccountTypes.Asset).ToList();
        _liabilities = all.Where(a => a.Type == BaseAccountTypes.Liability).ToList();
        _equity = all.Where(a => a.Type == BaseAccountTypes.Equity).ToList();
        _date = DateOnly.FromDateTime(DateTime.Today);
        Setup();
        OnPropertyChanged(nameof(Date));
    }

    public void Recalculate()
    {
        Assets.Recalculate(_startDate, Date);
        Liabilities.Recalculate(_startDate, Date);
        Equity.Recalculate(_startDate, Date);
        SetBalancedString();
    }

    private void Setup()
    {
        Assets.SetInfo(_dataStore, _taskFactory, "Assets", _assets, _startDate, Date, true);
        Liabilities.SetInfo(_dataStore, _taskFactory, "Liabilities", _liabilities, _startDate, Date, true);
        Equity.SetInfo(_dataStore, _taskFactory, "Equity", _equity, _startDate, Date, true);
        SetBalancedString();
    }

    private void SetBalancedString()
    {
        Balanced = (Assets.Total - Liabilities.Total) == Equity.Total ? "Balanced" : "Not Balanced";
    }
}