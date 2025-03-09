using System.Collections.ObjectModel;
using Accounting101.WPF.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using DataAccess.WPF;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

#pragma warning disable CS8618, CS9264

namespace Accounting101.WPF.Views.Read.Reports;

[ObservableObject]
public partial class ReportSectionView
{
    public string SectionHeader { get; private set; }

    public ReadOnlyObservableCollection<AccountsViewLine> Accounts { get; private set; }

    public decimal Total
    {
        get => _total;
        set
        {
            _total = value;
            OnPropertyChanged();
        }
    }

    private decimal _total;
    private DateOnly _startDate;
    private DateOnly _endDate;
    private IDataStore _dataStore;
    private JoinableTaskFactory _taskFactory;
    private List<AccountWithInfo> _accounts;
    private bool _isBalanceReport;

    public ReportSectionView()
    {
        DataContext = this;
        InitializeComponent();
    }

    public void SetInfo(
        IDataStore dataStore,
        JoinableTaskFactory taskFactory,
        string sectionHeader,
        List<AccountWithInfo> accounts,
        DateOnly startDate,
        DateOnly endDate,
        bool isBalanceReport)
    {
        _dataStore = dataStore;
        _taskFactory = taskFactory;
        _accounts = accounts;
        _isBalanceReport = isBalanceReport;
        SectionHeader = sectionHeader;
        OnPropertyChanged(nameof(SectionHeader));
        Recalculate(startDate, endDate);
    }

    public void Recalculate(DateOnly startDate, DateOnly endDate)
    {
        if (_isBalanceReport)
        {
            _endDate = endDate;
            CalculateBalances();
        }
        else
        {
            _startDate = startDate;
            _endDate = endDate;
            CalculateChanges();
        }
    }

    private void CalculateBalances()
    {
        List<AccountsViewLine> accounts = [];
        _total = 0;
        _accounts.ForEach(a =>
        {
            decimal balance = _taskFactory.Run(() => _dataStore.GetAccountBalanceOnDateAsync(a.Id, _endDate));
            _total += balance;
            accounts.Add(new AccountsViewLine
            {
                CoAId = a.Info.CoAId,
                Created = a.Created,
                CurrentBalance = balance,
                Id = a.Id,
                Name = a.Info.Name,
                Type = a.Type,
                StartBalance = a.StartBalance
            });
        });
        Accounts = new ReadOnlyObservableCollection<AccountsViewLine>(
            new ObservableCollection<AccountsViewLine>(accounts.OrderBy(a => a.CoAId)));
        OnPropertyChanged(nameof(Accounts));
        OnPropertyChanged(nameof(Total));
    }

    private void CalculateChanges()
    {
        List<AccountsViewLine> accounts = [];
        _total = 0;
        _accounts.ForEach(a =>
        {
            decimal startBalance = _taskFactory.Run(() => _dataStore.GetAccountBalanceOnDateAsync(a.Id, _startDate));
            decimal endBalance = _taskFactory.Run(() => _dataStore.GetAccountBalanceOnDateAsync(a.Id, _endDate));
            decimal change = endBalance - startBalance;
            _total += change;
            accounts.Add(new AccountsViewLine
            {
                CoAId = a.Info.CoAId,
                Created = a.Created,
                CurrentBalance = change,
                Id = a.Id,
                Name = a.Info.Name,
                Type = a.Type,
                StartBalance = a.StartBalance
            });
        });
        Accounts = new ReadOnlyObservableCollection<AccountsViewLine>(
            new ObservableCollection<AccountsViewLine>(accounts.OrderBy(a => a.CoAId)));
        OnPropertyChanged(nameof(Accounts));
        OnPropertyChanged(nameof(Total));
    }
}