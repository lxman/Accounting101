namespace Accounting101.Interfaces
{
    public interface ISavable
    {
        Task<bool> SaveAsync();
    }
}