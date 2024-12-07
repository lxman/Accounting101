using System.Collections.ObjectModel;
using Accounting101.Views.Single;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.ViewModels
{
    public class CreateAccountViewModel
    {
        public ObservableCollection<ClientView> Clients { get; }

        public Guid SelectedClientId { get; set; }

        public string Name { get; set; }

        public ObservableCollection<BaseAccountTypes> AccountTypes { get; }

        public BaseAccountTypes SelectedAccountType { get; set; }

        public string CoAId { get; set; }

        public decimal StartBalance { get; set; }

        private Account _account;
        private AccountInfo _info;

        public CreateAccountViewModel(IDataStore dataStore, JoinableTaskFactory taskFactory)
        {
            Clients = new ObservableCollection<ClientView>(taskFactory.Run(dataStore.AllClientsAsync)?.Select(c => new ClientView(dataStore, taskFactory, c))!);
            AccountTypes = new ObservableCollection<BaseAccountTypes>(
            [
                BaseAccountTypes.Asset,
                BaseAccountTypes.Expense,
                BaseAccountTypes.Liability,
                BaseAccountTypes.Equity,
                BaseAccountTypes.Revenue
            ]);
        }
    }
}
