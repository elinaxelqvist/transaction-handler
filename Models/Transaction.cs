using System;

namespace Labb2.Models
{
    public class Transaction
    {
        public int TransactionID { get; set; }
        public DateTime BookingDate { get; set; }
        public DateTime TransactionDate { get; set; }
        public required string Reference { get; set; }
        public decimal Amount { get; set; }
        public decimal Balance { get; set; }
        public required string Category { get; set; }

    }
}
