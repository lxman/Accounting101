using System.Windows;
using System.Windows.Input;
using Accounting101.Commands;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.ViewModels
{
    public class MenuViewModel : BaseViewModel
    {
        public ICommand NewCommand { get; }

        public bool ShowNewCommand
        {
            get => _showNewCommand;
            set => SetField(ref _showNewCommand, value);
        }

        public ICommand NewBusinessCommand { get; }

        public bool ShowNewBusinessCommand
        {
            get => _showNewBusinessCommand;
            set => SetField(ref _showNewBusinessCommand, value);
        }

        public ICommand NewClientCommand { get; }

        public bool ShowNewClientCommand
        {
            get => _showNewClientCommand;
            set => SetField(ref _showNewClientCommand, value);
        }

        public ICommand NewAccountCommand { get; }

        public bool ShowNewAccountCommand
        {
            get => _showNewAccountCommand;
            set => SetField(ref _showNewAccountCommand, value);
        }

        public ICommand NewTransactionCommand { get; }

        public bool ShowNewTransactionCommand
        {
            get => _showNewTransactionCommand;
            set => SetField(ref _showNewTransactionCommand, value);
        }

        public ICommand SaveCommand { get; set; }

        public bool ShowSaveCommand
        {
            get => _showSaveCommand;
            set => SetField(ref _showSaveCommand, value);
        }

        public ICommand ExitCommand { get; }

        private bool _showNewCommand = true;
        private bool _showNewBusinessCommand;
        private bool _showNewClientCommand;
        private bool _showNewAccountCommand;
        private bool _showNewTransactionCommand;
        private bool _showSaveCommand;
        private readonly IDataStore _dataStore;
        private readonly JoinableTaskFactory _taskFactory;

        public MenuViewModel(IDataStore dataStore, JoinableTaskFactory taskFactory)
        {
            _dataStore = dataStore;
            _dataStore.StoreChanged += StoreChanged;
            _taskFactory = taskFactory;
            ExitCommand = new DelegateCommand(() =>
            {
                _dataStore.Dispose();
                Application.Current.Shutdown();
            });
        }

        private static void StoreChanged(object? sender, ChangeEventArgs e)
        {
            if (e.ChangeType == ChangeType.Created)
            {
                if (e.ChangedType == typeof(Business))
                {
                }
                else if (e.ChangedType == typeof(Client))
                {
                }

                else if (e.ChangedType == typeof(Clients))
                {

                }
                else if (e.ChangedType == typeof(Account))
                {
                }

                else if (e.ChangedType == typeof(Accounts))
                {

                }
                else if (e.ChangedType == typeof(Transaction))
                {
                }
                else if (e.ChangedType == typeof(Transactions))
                {
                }
            }
            else
            {
                if (e.ChangedType == typeof(Transactions))
                {

                }

                if (e.ChangedType == typeof(Transaction))
                {

                }
                if (e.ChangedType == typeof(Accounts))
                {
                }
                if (e.ChangedType == typeof(Account))
                {
                }
                if (e.ChangedType == typeof(Clients))
                {
                }
                if (e.ChangedType == typeof(Client))
                {
                }
                if (e.ChangedType == typeof(Business))
                {
                }
            }
        }
    }
}
