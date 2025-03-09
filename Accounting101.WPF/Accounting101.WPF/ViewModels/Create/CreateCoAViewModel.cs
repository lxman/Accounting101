using DataAccess.WPF;
using DataAccess.WPF.CoATemplates.ChartList;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

#pragma warning disable CS8618, CS9264

namespace Accounting101.WPF.ViewModels.Create;

public class CreateCoAViewModel : BaseViewModel
{
    public event EventHandler? CoACreated;

    public string SelectedItem
    {
        get => _selectedItem;
        set
        {
            _selectedItem = value;
            CoASelected();
        }
    }

    public List<string> ChartItems { get; } = [];

    private string _selectedItem;
    private IDataStore _dataStore;
    private JoinableTaskFactory _taskFactory;
    private Client _client;
    private readonly Charts _chartList;

    public CreateCoAViewModel()
    {
        _chartList = new Charts();
        _chartList.ChartItems.ForEach(ci =>
        {
            string item = $"{ci.Name} - {ci.Description}";
            ChartItems.Add(item);
        });
        OnPropertyChanged(nameof(ChartItems));
    }

    public void SetInfo(IDataStore dataStore, JoinableTaskFactory taskFactory, Client client)
    {
        _dataStore = dataStore;
        _taskFactory = taskFactory;
        _client = client;
    }

    private void CoASelected()
    {
        string selectedChart = SelectedItem.Split(" - ")[0];
        AvailableCoAs coa = (AvailableCoAs)_chartList.ChartItems.Find(ci => ci.Name == selectedChart)?.Type!;
        _taskFactory.Run(() => _dataStore.CreateChartAsync(coa, _client));
        CoACreated?.Invoke(this, EventArgs.Empty);
    }
}