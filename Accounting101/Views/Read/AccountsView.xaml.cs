using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Media;
using Accounting101.Models;
using Accounting101.ViewModels.Read;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Read
{
    public partial class AccountsView : UserControl
    {
        public bool HasAccounts => _viewModel.HasAccounts;

        private readonly AccountsViewModel _viewModel = new();
        private readonly Brush _focusColor = Brushes.LightSlateGray;

        public AccountsView()
        {
            DataContext = _viewModel;
            InitializeComponent();
        }

        public void SetInfo(IDataStore dataStore, JoinableTaskFactory taskFactory, ClientWithInfo client)
        {
            _viewModel.SetInfo(dataStore, taskFactory, client);
        }

        private void DataGridAutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            PropertyDescriptor propertyDescriptor = (PropertyDescriptor)e.PropertyDescriptor;
            e.Column.Header = propertyDescriptor.DisplayName;
            if (propertyDescriptor.DisplayName == "Id")
            {
                e.Cancel = true;
            }
        }

        private void AccountListLoadingRow(object? sender, DataGridRowEventArgs e)
        {
            e.Row.Background = Brushes.Transparent;
            e.Row.MouseEnter += (s, _) =>
            {
                e.Row.Background = _focusColor;
            };
            e.Row.MouseLeave += (s, _) =>
            {
                e.Row.Background = Brushes.Transparent;
            };
            e.Row.PreviewMouseDown += (s, _) =>
            {
                Guid accountId = ((AccountsViewLine)e.Row.Item).Id;
                _viewModel.ItemSelected(accountId);
            };
        }
    }
}