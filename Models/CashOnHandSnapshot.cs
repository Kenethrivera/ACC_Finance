namespace acc_finance.Models
{
    // Model for the Default View: Total Cash On Hand Snapshot
    public class CashOnHandSnapshot
    {
        public DateTime Date { get; set; }
        public decimal TotalCohAmount { get; set; }

        // Custodian Breakdown
        public decimal CoSisCora { get; set; }
        public decimal CoPtraEs { get; set; }
        public decimal CoPW { get; set; }

        // Pledges Reconciliation
        public decimal PledgesShouldBeTotal { get; set; }
        public decimal PledgesActualTotal { get; set; }
        public decimal ShouldReturn => PledgesShouldBeTotal - PledgesActualTotal;
    }

    // Model for the Wallet Filters (General, Pledges, Construction)
    public class WalletLedgerEntry
    {
        public DateTime Date { get; set; }
        public string Description { get; set; } // e.g., "Net balance", "Less: Pledges", "Balance - Nov 2025"
        public decimal Amount { get; set; }
        public decimal RunningBalance { get; set; }
    }
}