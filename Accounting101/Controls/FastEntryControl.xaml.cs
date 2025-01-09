using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Controls;
using Accounting101.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using DataAccess.Models;
#pragma warning disable CS8618, CS9264

namespace Accounting101.Controls
{
    [ObservableObject]
    public partial class FastEntryControl : UserControl
    {
        public ReadOnlyObservableCollection<string>? OtherAccounts { get; private set; }

        public DateOnly When
        {
            get => _when;
            set => SetProperty(ref _when, value);
        }

        public bool Credit
        {
            get => _credit;
            set => SetProperty(ref _credit, value);
        }

        public bool Debit
        {
            get => _debit;
            set => SetProperty(ref _debit, value);
        }

        public string OtherAccount
        {
            get => _otherAccount;
            set => SetProperty(ref _otherAccount, value);
        }

        public string Amount
        {
            get => _amount.ToString(CultureInfo.CurrentCulture);
            set => SetProperty(ref _amount, decimal.Parse(value));
        }

        private Guid _id;
        private DateOnly _when;
        private bool _credit;
        private bool _debit;
        private string _otherAccount;
        private decimal _amount;

        public FastEntryControl()
        {
            DataContext = this;
            InitializeComponent();
        }

        public void CreateNew(List<AccountWithInfo> otherAccounts)
        {

        }

        public void EditEntry(TransactionInfoLine entry, List<AccountWithInfo> otherAccounts)
        {
            List<string> others = [];
            otherAccounts.ForEach(a =>
            {
                others.Add($"{a.Info.CoAId} {a.Info.Name} {a.Type}");
            });
            others = others.Order().ToList();
            OtherAccounts ??= new ReadOnlyObservableCollection<string>(new ObservableCollection<string>(others));
            _id = entry.Id;
            When = entry.When;
            Credit = entry.Credit.HasValue;
            Debit = entry.Debit.HasValue;
            AccountWithInfo? otherAccount = otherAccounts.Find(a => a.Info.CoAId == entry.OtherAccountInfo.Split(' ')[0]);
            if (otherAccount is not null)
            {
                OtherAccount = $"{otherAccount.Info.CoAId} {otherAccount.Info.Name} {otherAccount.Type}";
            }
            Amount = entry.Credit.HasValue ? entry.Credit.Value.ToString(CultureInfo.CurrentCulture) : entry.Debit.HasValue ? entry.Debit.Value.ToString(CultureInfo.CurrentCulture) : "0";
        }

        public TransactionInfoLine GetResult()
        {
            return new TransactionInfoLine(_id, When, Credit ? _amount : null, Debit ? _amount : null, 0, OtherAccount);
        }
    }
}
