using System;
using System.Collections.Generic;

namespace Labb2.Models
{
    public class TransactionSummary
    {
        public string Category { get; set; } = string.Empty;
        public int TransactionCount { get; set; }
        public decimal TotalAmount { get; set; }
        public bool IsIncome => TotalAmount > 0;
        public decimal Percentage { get; set; }
    }

    public class AccountSummary
    {
        public decimal TotalIncome { get; set; }
        public decimal TotalExpenses { get; set; }
        public decimal NetBalance => TotalIncome + TotalExpenses;
        public List<TransactionSummary> Categories { get; set; } = new List<TransactionSummary>();
    }
} 