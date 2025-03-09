using CommunityToolkit.Mvvm.ComponentModel;
using DataAccess.WPF.Models;

#pragma warning disable CS8618, CS9264

namespace Accounting101.WPF.Views.Update;

[ObservableObject]
public partial class UpdateForeignAddressView
{
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

    public string Country
    {
        get => _country;
        set
        {
            _country = value;
            OnPropertyChanged();
        }
    }

    public string Province
    {
        get => _province;
        set
        {
            _province = value;
            OnPropertyChanged();
        }
    }

    public string PostalCode
    {
        get => _postalCode;
        set
        {
            _postalCode = value;
            OnPropertyChanged();
        }
    }

    private string _line1;
    private string _line2;
    private string _country;
    private string _province;
    private string _postalCode;
    private Guid _id;

    public UpdateForeignAddressView()
    {
        DataContext = this;
        InitializeComponent();
    }

    public void SetAddress(ForeignAddress address)
    {
        Line1 = address.Line1;
        Line2 = address.Line2;
        Country = address.Country;
        Province = address.Province;
        PostalCode = address.PostalCode;
        _id = address.Id;
    }

    public ForeignAddress GetAddress()
    {
        return new ForeignAddress
        {
            Id = _id,
            Country = Country,
            Line1 = Line1,
            Line2 = Line2,
            Province = Province,
            PostalCode = PostalCode
        };
    }
}