﻿using System.Collections.Generic;

namespace DataAccess.CoATemplates.ChartList
{
    public class Charts
    {
        public List<ChartItem> ChartItems { get; } = [];

        public Charts()
        {
            const AvailableCoAs smallBusinessCoA = AvailableCoAs.SmallBusiness;
            ChartItems.Add(new ChartItem { Name = smallBusinessCoA.ToString(), Description = SmallBusiness.Description, Type = smallBusinessCoA });
        }
    }
}