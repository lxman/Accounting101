using Accounting101.WPF.ViewModels.Create;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.WPF.Views.Create;

public partial class NewAccountView
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