using Accounting101.Controls;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;
#pragma warning disable CS8618, CS9264

namespace Accounting101.ViewModels
{
    public class CreateTransactionViewModel
    {
        public Transaction Transaction { get; set; }

        public DateTime When { get; set; }

        public decimal Amount { get; set; }

        public AccountPickerControl DebitingAccountPicker { get; }

        public AccountPickerControl CreditingAccountPicker { get; }

        private Guid _debitingAccountId;
        private Guid _creditingAccountId;
        private readonly Guid _referenceAccountId;

        public CreateTransactionViewModel(
            IDataStore dataStore,
            JoinableTaskFactory taskFactory,
            Guid clientId,
            Guid acctId)
        {
            _referenceAccountId = acctId;
            When = DateTime.Today;
            CreditingAccountPicker = new AccountPickerControl(dataStore, taskFactory, clientId, acctId);
            CreditingAccountPicker.AccountSelected += CreditingAccountChosen;
            DebitingAccountPicker = new AccountPickerControl(dataStore, taskFactory, clientId, acctId);
            DebitingAccountPicker.AccountSelected += DebitingAccountChosen;
        }

        private void CreditingAccountChosen(object? sender, Guid accountId)
        {
            _creditingAccountId = accountId;
            CreditingAccountPicker.FreezeSelection();
            DebitingAccountPicker.PopulateOthers();
        }

        private void DebitingAccountChosen(object? sender, Guid accountId)
        {
            _debitingAccountId = accountId;
            DebitingAccountPicker.FreezeSelection();
            CreditingAccountPicker.PopulateOthers();
        }
    }
}
