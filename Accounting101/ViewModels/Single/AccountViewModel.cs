using System.Collections.ObjectModel;
using Accounting101.Controls;
using Accounting101.Messages;
using Accounting101.Models;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.ViewModels.Single
{
    public class AccountViewModel : BaseViewModel, IRecipient<CreateTransactionMessage>, IRecipient<UpdateTransactionMessage>, IRecipient<DeleteTransactionMessage>
    {
        public event EventHandler<AccountColumnWidthModel>? SetColumnWidths;

        public AccountHeaderControl AccountHeaderControl { get; }

        public ObservableCollection<LedgerLineControl> Transactions { get; } = [];

        private readonly JoinableTaskFactory _taskFactory;
        private readonly IDataStore _dataStore;
        private readonly AccountWithTransactions _f;
        private readonly AccountWithInfoFlat _a;
        private int _layoutCount;

        public AccountViewModel(
            IDataStore dataStore,
            JoinableTaskFactory taskFactory,
            AccountWithTransactions f,
            AccountWithInfoFlat a)
        {
            Messenger.Register<CreateTransactionMessage>(this);
            Messenger.Register<UpdateTransactionMessage>(this);
            Messenger.Register<DeleteTransactionMessage>(this);
            _dataStore = dataStore;
            _taskFactory = taskFactory;
            _f = f;
            _a = a;
            AccountHeaderControl = new AccountHeaderControl(a);
            PopulateTransactionsList();
        }

        public void Unregister()
        {
            Messenger.UnregisterAll(this);
        }

        public void Receive(CreateTransactionMessage message)
        {
            _taskFactory.Run(() => _dataStore.CreateTransactionAsync(message.Value));
            _f.Transactions.Add(message.Value);
            PopulateTransactionsList();
        }

        public void Receive(UpdateTransactionMessage message)
        {
            _taskFactory.Run(() => _dataStore.UpdateTransactionAsync(message.Value));
            _f.Transactions.RemoveAll(t => t.Id == message.Value.Id);
            _f.Transactions.Add(message.Value);
            PopulateTransactionsList();
        }

        public void Receive(DeleteTransactionMessage message)
        {
            _taskFactory.Run(() => _dataStore.DeleteTransactionAsync(message.Value.Id));
            _f.Transactions.RemoveAll(t => t.Id == message.Value.Id);
            PopulateTransactionsList();
        }

        public void ShowClientAccountsView()
        {
            Messenger.Send(new ChangeScreenMessage(WindowType.ClientAccountList));
        }

        private void PopulateTransactionsList()
        {
            Transactions.Clear();
            decimal balance = _a.StartBalance;
            List<LedgerLineControl> lines = [];
            _f.Transactions.OrderBy(t => t.When).ToList().ForEach(t =>
            {
                Guid otherAccountId = t.DebitedAccountId == _a.Id ? t.CreditedAccountId : t.DebitedAccountId;
                AccountWithInfo otherAccount = _taskFactory.Run(() => _dataStore.GetAccountWithInfoAsync(otherAccountId))!;
                if (t.CreditedAccountId == _a.Id)
                {
                    if (_a.IsDebitAccount) balance -= t.Amount;
                    else balance += t.Amount;
                }
                else
                {
                    if (_a.IsDebitAccount) balance += t.Amount;
                    else balance -= t.Amount;
                }
                lines.Add(new LedgerLineControl(t, balance, otherAccount));
            });
            lines.ForEach(l =>
            {
                Transactions.Add(l);
                l.LayoutUpdated += (s, e) => RecordAndLayoutIfFinished(s as LedgerLineControl, lines);
            });
            _layoutCount = 0;
            AccountHeaderControl.UpdateBalance(balance);
        }

        private void RecordAndLayoutIfFinished(LedgerLineControl? llc, List<LedgerLineControl> lines)
        {
            _layoutCount++;
            if (_layoutCount == lines.Count)
            {
                UpdateColumnWidths(lines);
            }
        }

        private void UpdateColumnWidths(List<LedgerLineControl> lines)
        {
            double dateWidth = lines.Max(l => l.DateBlock.ActualWidth);
            double creditWidth = lines.Max(l => l.CreditBlock.ActualWidth);
            double debitWidth = lines.Max(l => l.DebitBlock.ActualWidth);
            double balanceWidth = lines.Max(l => l.BalanceBlock.ActualWidth);
            lines.ForEach(l =>
            {
                l.DateBlock.MinWidth = dateWidth;
                l.DateBlock.MaxWidth = dateWidth;
                l.DateBlock.Width = dateWidth;
                l.CreditBlock.MinWidth = creditWidth;
                l.CreditBlock.MaxWidth = creditWidth;
                l.CreditBlock.Width = creditWidth;
                l.DebitBlock.MinWidth = debitWidth;
                l.DebitBlock.MaxWidth = debitWidth;
                l.DebitBlock.Width = debitWidth;
                l.BalanceBlock.MinWidth = balanceWidth;
                l.BalanceBlock.MaxWidth = balanceWidth;
                l.BalanceBlock.Width = balanceWidth;
            });
            SetColumnWidths?.Invoke(this, new AccountColumnWidthModel
            {
                DateWidth = dateWidth,
                DebitWidth = debitWidth,
                CreditWidth = creditWidth,
                BalanceWidth = balanceWidth
            });
        }
    }
}