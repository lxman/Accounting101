using System.Windows.Controls;
using Accounting101.ViewModels.Create;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Create
{
    public partial class NewAccountView : UserControl
    {
        private readonly NewAccountViewModel _viewModel = new();

        public NewAccountView()
        {
            DataContext = _viewModel;
            InitializeComponent();
        }

        public void SetInfo(IDataStore dataStore, JoinableTaskFactory taskFactory, Client client)
        {
            _viewModel.SetInfo(dataStore, taskFactory, client);
        }

        public void Save()
        {
            _viewModel.Save();
        }
    }
}