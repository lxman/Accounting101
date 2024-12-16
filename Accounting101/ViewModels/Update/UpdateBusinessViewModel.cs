using System.Windows.Controls;
using Accounting101.Messages;
using Accounting101.ViewModels.Single;
using Accounting101.Views.Update;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

#pragma warning disable CS8618, CS9264

namespace Accounting101.ViewModels.Update
{
    public class UpdateBusinessViewModel : BaseViewModel, IRecipient<SaveMessage>
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

        public UpdateBusinessViewModel(IDataStore dataStore, JoinableTaskFactory taskFactory)
        {
            WeakReferenceMessenger.Default.Register(this);
            _dataStore = dataStore;
            _taskFactory = taskFactory;
            Business? found = taskFactory.Run(() => _dataStore.GetBusinessAsync());
            Business ??= found ?? new Business { Name = string.Empty };
            _foreignCheckboxState = Business.Address is ForeignAddress;
            if (_foreignCheckboxState)
            {
                AddressView = new UpdateForeignAddressView(_dataStore, taskFactory, Business.Address.Id);
                Business.Address = (AddressView.DataContext as ForeignAddressViewModel)!.Address;
            }
            else
            {
                AddressView = new UpdateUSAddressView(_dataStore, taskFactory, Business.Address.Id);
                Business.Address = (AddressView.DataContext as USAddressViewModel)!.Address;
            }
        }

        public void Receive(SaveMessage message)
        {
            if (message.Value != WindowType.EditBusiness)
            {
                return;
            }
            _taskFactory.Run(SaveAsync);
            Messenger.Send(new ChangeScreenMessage(WindowType.CreateClient));
        }

        public async Task<bool> SaveAsync()
        {
            _ = await _dataStore.UpdateAddressAsync(Business!.Address);
            await _dataStore.UpdateBusinessAsync(Business);
            return true;
        }

        private void ForeignCheckboxChangeState(bool state)
        {
            if (state)
            {
                Business!.Address = new ForeignAddress();
                AddressView = new UpdateForeignAddressView(_dataStore, _taskFactory, Business.Address.Id);
            }
            else
            {
                Business!.Address = new UsAddress();
                AddressView = new UpdateUSAddressView(_dataStore, _taskFactory, Business.Address.Id);
            }
        }
    }
}