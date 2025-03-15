using Accounting101.Angular.DataAccess.Extensions;

namespace Accounting101.Angular.DataAccess.CoATemplates.ChartList;

public class ChartItem
{
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string ReadableType => Type.GetDescription();

    public AvailableCoAs Type { get; set; }
}