using Accounting101.Angular.DataAccess.Interfaces;
using MongoDB.Bson;

namespace Accounting101.Angular.DataAccess.Models;

public class Business
{
    public ObjectId Id { get; set; } = new();

    public string Name { get; set; } = string.Empty;

    public IAddress? Address { get; set; }
}