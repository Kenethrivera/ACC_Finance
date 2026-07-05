using System.Collections.Generic;

namespace acc_finance.Models
{
    public class VerificationLedger
    {
        public string ReportMonth { get; set; }

        // Holds points 1-5 (Daily Blessings, Disbursements, Returns)
        public List<DailyTransactionSummary> DailySummaries { get; set; } = new();

        // Holds point 6 (Overall Math)
        public OverallSummary Overall { get; set; } = new();

        // Holds point 7 (Individual Wallet Math)
        public Dictionary<string, FundAudit> FundAudits { get; set; } = new();

        public DashboardTotals SystemDashboard { get; set; } = new();
    }

    public class DailyTransactionSummary
    {
        public string Date { get; set; }
        public string DateId { get; set; }
        public decimal TotalBlessing { get; set; }
        public Dictionary<string, decimal> BlessingByWallet { get; set; } = new();

        public decimal TotalDisbursement { get; set; }
        public decimal TotalCashReturn { get; set; }
        public decimal AdjustedDisbursement { get; set; }
        public Dictionary<string, decimal> DisbursementByWallet { get; set; } = new();
    }

    public class OverallSummary
    {
        public decimal BeginningBalance { get; set; }
        public decimal TotalGiving { get; set; }
        public decimal TotalAdjustedDisbursement { get; set; }
        public decimal NetCashBalance { get; set; }
    }

    public class FundAudit
    {
        public decimal BeginningBalance { get; set; }
        public decimal TotalIncome { get; set; }
        public decimal TotalDisbursements { get; set; }
        public decimal CalculatedEndingBalance { get; set; }
        public bool IsMathBalanced { get; set; }
        public bool IsInDeficit { get; set; }
    }

    public class DashboardTotals
    {
        public decimal BookBalance { get; set; }
        public decimal CashOnHand { get; set; }
    }
}