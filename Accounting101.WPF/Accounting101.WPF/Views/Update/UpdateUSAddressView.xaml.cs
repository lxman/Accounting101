using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DataAccess.WPF.Models;

#pragma warning disable CS8618, CS9264

namespace Accounting101.WPF.Views.Update;

[ObservableObject]
public partial class UpdateUSAddressView
{
    public ReadOnlyObservableCollection<string> States { get; private set; }

    public string Line1
    {
        get => _line1;
        set
        {
            _line1 = value;
            OnPropertyChanged();
        }
    }

    public string Line2
    {
        get => _line2;
        set
        {
            _line2 = value;
            OnPropertyChanged();
        }
    }

    public string City
    {
        get => _city;
        set
        {
            _city = value;
            OnPropertyChanged();
        }
    }

    public string State
    {
        get => _state;
        set
        {
            _state = value;
            OnPropertyChanged();
        }
    }

    public string Zip
    {
        get => _zip;
        set
        {
            _zip = value;
            OnPropertyChanged();
        }
    }

    private string _line1 = string.Empty;
    private string _line2 = string.Empty;
    private string _city = string.Empty;
    private string _state = string.Empty;
    private string _zip = string.Empty;
    private Guid _id;

    public UpdateUSAddressView()
    {
        DataContext = this;
        InitializeComponent();
    }

    public void SetAddress(UsAddress? address)
    {
        if (address != null)
        {
            _id = address.Id;
            Line1 = address.Line1;
            Line2 = address.Line2;
            City = address.City;
            State = address.State;
            Zip = address.Zip;
        }
    }

    public void SetStates(List<string> states)
    {
        States = new ReadOnlyObservableCollection<string>(new ObservableCollection<string>(states));
        OnPropertyChanged(nameof(States));
    }

    public UsAddress GetAddress()
    {
        return new UsAddress
        {
            Id = _id,
            Line1 = Line1,
            Line2 = Line2,
            City = City,
            State = State,
            Zip = Zip
        };
    }
}