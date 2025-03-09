using System.Windows;
using System.Windows.Controls;
using Accounting101.WPF.Messages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.WPF.Views.Create;

[ObservableObject]
public partial class CreateBusinessView
{
    public string BusinessName { get; set; } = string.Empty;

    public static readonly DependencyProperty AddressViewProperty = DependencyProperty.Register(
        nameof(AddressView), typeof(UserControl), typeof(CreateBusinessView), new PropertyMetadata(default(UserControl)));

    public UserControl AddressView
    {
        get => (UserControl)GetValue(AddressViewProperty);
        set => SetValue(AddressViewProperty, value);
    }

    public bool? Foreign
    {
        get => _foreign;
        set
        {
            _foreign = value;
            UpdateAddressView();
        }
    }

    private readonly CreateUSAddressView _createUSAddressView = new();
    private readonly CreateForeignAddressView _createForeignAddressView = new();
    private bool? _foreign = false;
    private readonly IDataStore _dataStore;
    private readonly JoinableTaskFactory _taskFactory;

    public CreateBusinessView(IDataStore dataStore, JoinableTaskFactory taskFactory)
    {
        List<string> states = taskFactory.Run(dataStore.GetStatesAsync).Order().ToList();
        _dataStore = dataStore;
        _taskFactory = taskFactory;
        DataContext = this;
        InitializeComponent();
        _createUSAddressView.SetStates(states);
        AddressView = _createUSAddressView;
    }

    public void Save()
    {
        Business business = new()
        {
            Name = BusinessName,
            Address = _foreign switch
            {
                false => _createUSAddressView.GetResult(),
                true => _createForeignAddressView.GetResult(),
                _ => null
            }
        };
        _taskFactory.Run(() => _dataStore.CreateBusinessAsync(business));
        WeakReferenceMessenger.Default.Send(new ChangeScreenMessage(WindowType.CreateClient));
    }

    private void UpdateAddressView()
    {
        AddressView = _foreign switch
        {
            false => _createUSAddressView,
            true => _createForeignAddressView,
            _ => AddressView
        };
    }
}