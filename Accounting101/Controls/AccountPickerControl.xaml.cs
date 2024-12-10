using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Accounting101.Models;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Controls
{
    public partial class AccountPickerControl : UserControl
    {
        public event EventHandler<Guid>? AccountSelected;

        public ObservableCollection<AccountPickerLine> Accounts { get; }

        public static readonly DependencyProperty ComboVisibleProperty = DependencyProperty.Register(
            nameof(ComboVisible), typeof(bool), typeof(AccountPickerControl), new PropertyMetadata(default(bool)));

        public bool ComboVisible
        {
            get => (bool)GetValue(ComboVisibleProperty);
            set => SetValue(ComboVisibleProperty, value);
        }

        public static readonly DependencyProperty TextBlockVisibleProperty = DependencyProperty.Register(
            nameof(TextBlockVisible), typeof(bool), typeof(AccountPickerControl), new PropertyMetadata(default(bool)));

        public bool TextBlockVisible
        {
            get => (bool)GetValue(TextBlockVisibleProperty);
            set => SetValue(TextBlockVisibleProperty, value);
        }

        public static readonly DependencyProperty SelectedAccountProperty = DependencyProperty.Register(
            nameof(SelectedAccount), typeof(AccountPickerLine), typeof(AccountPickerControl), new PropertyMetadata(default(AccountPickerLine?)));

        public AccountPickerLine? SelectedAccount
        {
            get => (AccountPickerLine?)GetValue(SelectedAccountProperty);
            set => SetValue(SelectedAccountProperty, value);
        }

        public Guid SelectedAccountId
        {
            get => _selectedAccountId;
            set
            {
                _selectedAccountId = value;
                AccountSelected?.Invoke(this, value);
            }
        }

        private Guid _selectedAccountId;
        private readonly List<AccountWithInfo> _accounts;
        private readonly Guid _homeAcct;

        public AccountPickerControl(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid clientId, Guid acctId)
        {
            ComboVisible = true;
            TextBlockVisible = false;
            _homeAcct = acctId;
            _accounts = taskFactory.Run(() => dataStore.AccountsForClientAsync(clientId))?.ToList()
                ?? throw new InvalidOperationException("Failed to load accounts");
            Accounts = new ObservableCollection<AccountPickerLine>(_accounts.Where(a => a.Id == acctId).Select(a => new AccountPickerLine(a)));
            DataContext = this;
            InitializeComponent();
        }

        public void FreezeSelection()
        {
            SelectedAccount = Accounts.FirstOrDefault(a => a.Id == _selectedAccountId);
            ComboVisible = false;
            TextBlockVisible = true;
        }

        public void PopulateOthers()
        {
            Accounts.Clear();
            foreach (AccountWithInfo a in _accounts.Where(a => a.Id != _homeAcct))
            {
                Accounts.Add(new AccountPickerLine(a));
            }
        }
    }
}