using Accounting101.WPF.ViewModels.Create;
using DataAccess.WPF.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.WPF.Views.Create;

public partial class CreateCheckPointView
{
    public CreateCheckPointViewModel ViewModel { get; } = new();

    public CreateCheckPointView()
    {
        DataContext = ViewModel;
        InitializeComponent();
    }

    public void SetInfo(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid clientId)
    {
        ViewModel.SetInfo(dataStore, taskFactory, clientId);
    }

    public void Save()
    {
        ViewModel.Save();
    }
}