using DataAccess.WPF.Models;

namespace Accounting101.WPF.ViewModels.Create;

public class CreateForeignAddressViewModel : BaseViewModel
{
    public string Line1
    {
        get => _line1;
        set => SetField(ref _line1, value);
    }

    public string Line2
    {
        get => _line2;
        set => SetField(ref _line2, value);
    }

    public string Country
    {
        get => _country;
        set => SetField(ref _country, value);
    }

    public string Province
    {
        get => _province;
        set => SetField(ref _province, value);
    }

    public string PostalCode
    {
        get => _postalCode;
        set => SetField(ref _postalCode, value);
    }

    private string _line1 = string.Empty;
    private string _line2 = string.Empty;
    private string _country = string.Empty;
    private string _province = string.Empty;
    private string _postalCode = string.Empty;

    public ForeignAddress GetResult()
    {
        return new ForeignAddress
        {
            Line1 = Line1,
            Line2 = Line2,
            Country = Country,
            Province = Province,
            PostalCode = PostalCode
        };
    }
}