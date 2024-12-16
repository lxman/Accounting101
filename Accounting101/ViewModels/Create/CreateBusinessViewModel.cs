using System.Windows.Controls;
using Accounting101.Messages;
using Accounting101.ViewModels.Single;
using Accounting101.Views.Create;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

#pragma warning disable CS8618, CS9264

namespace Accounting101.ViewModels.Create
{
    public class CreateBusinessViewModel : BaseViewModel, IRecipient<SaveMessage>
    {
        public UserControl AddressView
        {
            get => _addressView;
            private set => SetField(ref _addressView, value);
        }

        public bool ForeignCheckboxState
        {
            get => _foreignCheckboxState;
            set
            {
                ForeignCheckboxChangeState(value);
                _foreignCheckboxState = value;
            }
        }

        public Business? Business
        {
            get => _business;
            set => SetField(ref _business, value);
        }

        private Business? _business;
        private bool _foreignCheckboxState;
        private readonly IDataStore _dataStore;
        private UserControl _addressView;
        private readonly JoinableTaskFactory _taskFactory;

        public CreateBusinessViewModel(IDataStore dataStore, JoinableTaskFactory taskFactory)
        {
            WeakReferenceMessenger.Default.Register(this);
            _dataStore = dataStore;
            _taskFactory = taskFactory;
            Business? found = taskFactory.Run(() => _dataStore.GetBusinessAsync());
            Business ??= found ?? new Business { Name = string.Empty };
            _foreignCheckboxState = Business.Address is ForeignAddress;
            if (_foreignCheckboxState)
            {
                AddressView = new CreateForeignAddressView();
                Business.Address = (AddressView.DataContext as ForeignAddressViewModel)!.Address;
            }
            else
            {
                AddressView = new CreateUSAddressView(_dataStore, taskFactory);
                Business.Address = (AddressView.DataContext as USAddressViewModel)!.Address;
            }
        }

        public void Receive(SaveMessage message)
        {
            if (message.Value != WindowType.CreateBusiness)
            {
                return;
            }
            _taskFactory.Run(SaveAsync);
            Messenger.Send(new ChangeScreenMessage(WindowType.CreateClient));
        }

        public async Task<bool> SaveAsync()
        {
            _ = await _dataStore.CreateAddressAsync(Business!.Address);
            await _dataStore.CreateBusinessAsync(Business);
            return true;
        }

        private void ForeignCheckboxChangeState(bool state)
        {
            if (state)
            {
                Business!.Address = new ForeignAddress();
                AddressView = new CreateForeignAddressView();
            }
            else
            {
                Business!.Address = new UsAddress();
                AddressView = new CreateUSAddressView(_dataStore, _taskFactory);
            }
        }
    }
}