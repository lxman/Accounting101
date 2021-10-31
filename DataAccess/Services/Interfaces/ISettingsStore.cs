using LiteDB;

namespace DataAccess.Services.Interfaces
{
    public interface ISettingsStore
    {
        LiteDatabase? Instance();

        ILiteCollection<T>? GetCollection<T>(string name);

        BsonValue AddItem<T>(T item);

        void Dispose();
    }
}