using CommunityToolkit.Mvvm.ComponentModel;
using DataAccess.WPF;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.WPF.Views.Read.Reports;

[ObservableObject]
public partial class ProfitAndLossView
{
    public DateOnly StartDate
    {
        get => _startDate;
        set
        {
            if (value == _startDate)
            {
                return;
            }
            _startDate = value;
            OnPropertyChanged();
            SetDates(value, _endDate);
        }
    }

    public DateOnly EndDate
    {
        get => _endDate;
        set
        {
            if (value == _endDate)
            {
                return;
            }
            _endDate = value;
            OnPropertyChanged();
            SetDates(_startDate, value);
        }
    }

    public string ProfitLossText => GrandTotal >= 0 ? "Profit:" : "Loss:";

    public decimal GrandTotal
    {
        get => _grandTotal;
        set
        {
            if (value == _grandTotal)
            {
                return;
            }
            _grandTotal = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProfitLossText));
        }
    }

    private decimal _grandTotal;
    private DateOnly _startDate;
    private DateOnly _endDate;
    private readonly List<AccountWithInfo> _revenue;
    private readonly List<AccountWithInfo> _expenses;
    private readonly List<AccountWithInfo> _earnings;
    private readonly IDataStore _dataStore;
    private readonly JoinableTaskFactory _taskFactory;

    public ProfitAndLossView(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid clientId)
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
        _endDate = DateOnly.FromDateTime(DateTime.Today);
        OnPropertyChanged(nameof(StartDate));
        OnPropertyChanged(nameof(EndDate));
        _revenue = all.Where(a => a.Type == BaseAccountTypes.Revenue).ToList();
        _expenses = all.Where(a => a.Type == BaseAccountTypes.Expense).ToList();
        _earnings = all.Where(a => a.Type == BaseAccountTypes.Earnings).ToList();
        Setup();
    }

    private void Setup()
    {
        Revenue.SetInfo(_dataStore, _taskFactory, "Revenue", _revenue, _startDate, _endDate, false);
        Expenses.SetInfo(_dataStore, _taskFactory, "Expenses", _expenses, _startDate, _endDate, false);
        Earnings.SetInfo(_dataStore, _taskFactory, "Earnings", _earnings, _startDate, _endDate, false);
        GrandTotal = Revenue.Total - Expenses.Total + Earnings.Total;
    }

    public void SetDates(DateOnly start, DateOnly end)
    {
        Revenue.Recalculate(start, end);
        Expenses.Recalculate(start, end);
        Earnings.Recalculate(start, end);
        GrandTotal = Revenue.Total - Expenses.Total + Earnings.Total;
    }
}