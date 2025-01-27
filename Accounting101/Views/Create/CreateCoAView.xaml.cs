using Accounting101.ViewModels.Create;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Create
{
    public partial class CreateCoAView
    {
        public event EventHandler? CoACreated;

        private readonly CreateCoAViewModel _viewModel;

        public CreateCoAView()
        {
            _viewModel = new CreateCoAViewModel();
            _viewModel.CoACreated += (sender, args) => CoACreated?.Invoke(sender, args);
            DataContext = _viewModel;
            InitializeComponent();
        }

        public void SetInfo(IDataStore dataStore, JoinableTaskFactory taskFactory, Client client)
        {
            _viewModel.SetInfo(dataStore, taskFactory, client);
        }
    }
}