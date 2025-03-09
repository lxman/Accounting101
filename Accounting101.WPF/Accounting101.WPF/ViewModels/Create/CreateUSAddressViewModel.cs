using System.Collections.ObjectModel;
using DataAccess.WPF.Models;

namespace Accounting101.WPF.ViewModels.Create;

public class CreateUSAddressViewModel : BaseViewModel
{
    public ReadOnlyObservableCollection<string>? States { get; private set; }

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

    public string City
    {
        get => _city;
        set => SetField(ref _city, value);
    }

    public string State
    {
        get => _state;
        set => SetField(ref _state, value);
    }

    public string Zip
    {
        get => _zip;
        set => SetField(ref _zip, value);
    }

    private string _line1 = string.Empty;
    private string _line2 = string.Empty;
    private string _city = string.Empty;
    private string _state = string.Empty;
    private string _zip = string.Empty;

    public void SetStates(List<string> states)
    {
        States = new ReadOnlyObservableCollection<string>(new ObservableCollection<string>(states));
        OnPropertyChanged(nameof(States));
    }

    public UsAddress GetResult()
    {
        return new UsAddress
        {
            Line1 = Line1,
            Line2 = Line2,
            City = City,
            State = State,
            Zip = Zip
        };
    }
}