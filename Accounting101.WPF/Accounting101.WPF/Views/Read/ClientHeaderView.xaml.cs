using System.Windows.Input;
using Accounting101.WPF.Messages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess.WPF.Models;

namespace Accounting101.WPF.Views.Read;

[ObservableObject]
public partial class ClientHeaderView
{
    public string BusinessName { get; private set; } = string.Empty;

    public string Contact { get; private set; } = string.Empty;

    public string Address { get; private set; } = string.Empty;

    public string CheckPoint { get; private set; } = string.Empty;

    public ClientHeaderView()
    {
        DataContext = this;
        InitializeComponent();
    }

    public void SetInfo(ClientWithInfo client, CheckPoint? checkPoint)
    {
        BusinessName = client.BusinessName;
        Contact = client.Name?.ToString() ?? string.Empty;
        Address = client.Address?.ToString() ?? string.Empty;
        CheckPoint = checkPoint is null ? "None" : checkPoint.Date.ToString("MM/dd/yyyy");
        OnPropertyChanged(nameof(BusinessName));
        OnPropertyChanged(nameof(Contact));
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(CheckPoint));
    }

    private void ClientHeaderViewPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        WeakReferenceMessenger.Default.Send(new ChangeScreenMessage(WindowType.ClientList));
    }
}