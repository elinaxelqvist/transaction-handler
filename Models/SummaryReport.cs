using System;
using System.Collections.Generic;

namespace Labb2.Models
{
    public class CategorySummary
    {
        public string Category { get; set; } = string.Empty;
        public decimal TotalAmount { get; set; }
        public int TransactionCount { get; set; }
    }

    public class SummaryReport
    {
        public DateTime GeneratedAt { get; set; }
        public decimal TotalIncome { get; set; }
        public decimal TotalExpenses { get; set; }
        public decimal NetBalance { get; set; }
        public List<CategorySummary> IncomeCategories { get; set; } = new List<CategorySummary>();
        public List<CategorySummary> ExpenseCategories { get; set; } = new List<CategorySummary>();
    }
} 