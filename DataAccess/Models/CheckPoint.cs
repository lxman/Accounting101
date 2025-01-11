using System;
using System.Collections.Generic;

namespace DataAccess.Models
{
    public class CheckPoint(Guid clientId, DateOnly date)
    {
        public Guid Id { get; set; }

        public Guid ClientId { get; init; } = clientId;

        public DateOnly Date { get; init; } = date;

        public List<AccountCheckpoint> AccountCheckpoints { get; } = [];
    }
}
