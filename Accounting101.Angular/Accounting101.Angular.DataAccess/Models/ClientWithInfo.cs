﻿using System;
using System.Linq;
using Accounting101.Angular.DataAccess.Extensions;
using Accounting101.Angular.DataAccess.Interfaces;
using Accounting101.Angular.DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;
using MongoDB.Bson.Serialization.Attributes;

#pragma warning disable VSTHRD002

namespace Accounting101.Angular.DataAccess.Models;

public class ClientWithInfo : Client
{
    public PersonName? ContactName { get; set; }

    public IAddress? Address
    {
        get => _address;
        set
        {
            if (_address == value) return;
            _address = value;
            switch (value)
            {
                case UsAddress usAddress:
                    UsAddress = usAddress;
                    break;
                case ForeignAddress foreignAddress:
                    ForeignAddress = foreignAddress;
                    break;
            }
        }
    }

    [BsonIgnore]
    public UsAddress? UsAddress { get; set; }

    [BsonIgnore]
    public ForeignAddress? ForeignAddress { get; set; }

    private IAddress? _address;

    public ClientWithInfo()
    {

    }

    private readonly JoinableTaskFactory _jtf = new(new JoinableTaskCollection(new JoinableTaskContext()));

    public ClientWithInfo(Client c, PersonName n, IAddress a)
    {
        BusinessName = c.BusinessName;
        Id = c.Id;
        PersonNameId = n.Id.ToString();
        AddressId = a.Id.ToString();
        ContactName = n;
        Address = a;
        CheckPointId = c.CheckPointId;
    }

    public ClientWithInfo(IDataStore dataStore, string dbName, Client c) : base(c)
    {
        ContactName = _jtf.Run(() => dataStore.GetAllGlobalScopeAsync<PersonName>(dbName))?.FirstOrDefault(pn => pn.ClientId == c.Id.ToString());
        Address = dataStore.FindAddressById(dbName, Guid.Parse((ReadOnlySpan<char>)AddressId));
        ContactName = _jtf.Run(() => dataStore.FindNameByIdAsync(dbName, Guid.Parse((ReadOnlySpan<char>)PersonNameId)));
        CheckPointId = c.CheckPointId;
    }
}