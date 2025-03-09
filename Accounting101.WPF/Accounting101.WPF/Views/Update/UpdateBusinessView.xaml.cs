using Accounting101.WPF.ViewModels.Update;
using DataAccess.WPF.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.WPF.Views.Update;

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