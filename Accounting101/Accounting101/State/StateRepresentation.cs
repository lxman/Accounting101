namespace Accounting101.State;

public interface IStateRepresentation
{
    CurrentState State { get; set; }
}

public class StateRepresentation : IStateRepresentation
{
    public CurrentState State { get; set; }
}