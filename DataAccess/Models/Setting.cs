using System;

namespace DataAccess.Models
{
    public class Setting
    {
        public Guid Id { get; set; }

        public string Key { get; set; }

        public string Value { get; set; }
    }
}