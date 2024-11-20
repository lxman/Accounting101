using System.Collections.ObjectModel;
using DataAccess.Models;

namespace Accounting101.ViewModels
{
    public class StateSelectorViewModel
    {
        public string SelectedState { get; set; }

        public ReadOnlyObservableCollection<string> States { get; }

        public StateSelectorViewModel(UsStates states, string? state = null)
        {
            States = new ReadOnlyObservableCollection<string>(new ObservableCollection<string>(states.States));
            SelectedState = state ?? States[0];
        }
    }
}
