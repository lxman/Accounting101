using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Read.Reports
{
    [ObservableObject]
    public partial class BalanceSheetView : UserControl
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
            List<AccountWithInfo> all = taskFactory.Run(() => dataStore.AccountsForClientAsync(clientId))?.ToList() ?? [];
            _startDate = all.Min(a => a.Created);
            _assets = all.Where(a => a.Type == BaseAccountTypes.Asset).ToList();
            _liabilities = all.Where(a => a.Type == BaseAccountTypes.Liability).ToList();
            _equity = all.Where(a => a.Type == BaseAccountTypes.Equity).ToList();
            _date = DateOnly.FromDateTime(DateTime.Today);
            Setup();
            OnPropertyChanged(nameof(Date));
        }

        private void Setup()
        {
            Assets.SetInfo(_dataStore, _taskFactory, "Assets", _assets, _startDate, Date, true);
            Liabilities.SetInfo(_dataStore, _taskFactory, "Liabilities", _liabilities, _startDate, Date, true);
            Equity.SetInfo(_dataStore, _taskFactory, "Equity", _equity, _startDate, Date, true);
        }

        public void Recalculate()
        {
            Assets.Recalculate(_startDate, Date);
            Liabilities.Recalculate(_startDate, Date);
            Equity.Recalculate(_startDate, Date);
        }
    }
}
