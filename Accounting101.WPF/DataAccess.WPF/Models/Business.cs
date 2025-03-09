using DataAccess.WPF.Interfaces;
using LiteDB;

namespace DataAccess.WPF.Models;

public class Business
{
    public ObjectId Id { get; set; } = new();

    public string Name { get; set; } = string.Empty;

    public IAddress? Address { get; set; }
}