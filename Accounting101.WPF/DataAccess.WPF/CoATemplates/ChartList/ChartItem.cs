namespace DataAccess.WPF.CoATemplates.ChartList;

public class ChartItem
{
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public AvailableCoAs Type { get; set; }
}