﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace Birko.SuperFaktura.Entities
{
    public partial class Expense 
    {
        public class Payment
        {
            [JsonProperty(PropertyName = "expense_id", NullValueHandling = NullValueHandling.Ignore)]
            public int ExpenseID { get; set; }
            // payment consts
            [JsonProperty(PropertyName = "type", NullValueHandling = NullValueHandling.Ignore)]
            public string Type { get; set; } = Invoice.Payment.BankTransfer;
            [JsonProperty(PropertyName = "amount", NullValueHandling = NullValueHandling.Ignore)]
            public decimal Amount { get; set; } = 0;
            [JsonProperty(PropertyName = "currency", NullValueHandling = NullValueHandling.Ignore)]
            public string Currency { get; set; } = Invoice.Currency.Euro;
            [JsonProperty(PropertyName = "date", NullValueHandling = NullValueHandling.Ignore)]
            public DateTime Date { get; set; }
        }
    }
}
