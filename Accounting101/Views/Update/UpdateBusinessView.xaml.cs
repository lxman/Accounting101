using Accounting101.ViewModels.Update;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Update;

public partial class UpdateBusinessView
{
    private readonly UpdateBusinessViewModel _viewModel = new();

    public UpdateBusinessView()
    {
        DataContext = _viewModel;
        InitializeComponent();
    }

    public void SetInfo(IDataStore dataStore, JoinableTaskFactory taskFactory)
    {
        _viewModel.SetInfo(dataStore, taskFactory);
    }

    public void Save()
    {
        _viewModel.Save();
    }
}