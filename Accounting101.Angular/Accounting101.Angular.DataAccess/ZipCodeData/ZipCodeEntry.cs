using System;
using Accounting101.Angular.DataAccess.Interfaces;

namespace Accounting101.Angular.DataAccess.ZipCodeData;

public class ZipCodeEntry : IModel
{
    public Guid Id { get; set; }

    public string City { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public string Zip { get; set; } = string.Empty;

    public string AreaCode { get; set; } = string.Empty;

    public string Fips { get; set; } = string.Empty;

    public string County { get; set; } = string.Empty;
}