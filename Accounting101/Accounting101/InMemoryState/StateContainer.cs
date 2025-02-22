namespace Accounting101.InMemoryState;

public class StateContainer
{
    public event Action? OnChange;

    public string? Property { get; set; }

    public string? SelectedClient { get; set; }

    public void NotifyStateChanged() => OnChange?.Invoke();
}