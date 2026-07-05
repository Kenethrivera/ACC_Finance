namespace acc_finance.Models.Reports
{
    public class WeeklyFinancialReportVm
    {
        public string ChurchName { get; set; } = "ADELINA CHRISTIAN CHURCH";
        public DateTime ReportDate { get; set; }

        public GivingReportSectionVm Giving { get; set; } = new();
        public DenominationReportSectionVm Denomination { get; set; } = new();
        public DisbursementReportSectionVm Disbursement { get; set; } = new();
        public WeeklySummarySectionVm Summary { get; set; } = new();
        public decimal OverallBegBalance { get; set; }
    }


    public class GivingReportSectionVm
    {
        public long GivingRecordId { get; set; }
        public string RecordCode { get; set; } = string.Empty;
        public DateTime ServiceDate { get; set; }

        public List<GivingReportRowVm> Rows { get; set; } = new();

        public decimal TotalTithes { get; set; }
        public decimal TotalOfferings { get; set; }
        public decimal TotalSolomon { get; set; }
        public decimal TotalNoah { get; set; }
        public decimal TotalMission { get; set; }
        public decimal TotalOthers { get; set; }
        public decimal GrandTotal { get; set; }
    }

    public class GivingReportRowVm
    {
        public long? MemberId { get; set; }
        public string Name { get; set; } = string.Empty;

        public decimal Tithes { get; set; }
        public decimal Offerings { get; set; }
        public decimal Solomon { get; set; }
        public decimal Noah { get; set; }
        public decimal Mission { get; set; }
        public decimal Others { get; set; }
        public decimal Total { get; set; }

        public bool IsFamily { get; set; }
        public bool IsGroup { get; set; }
        public int SortGroup { get; set; }
    }

    public class DenominationReportSectionVm
    {
        public bool Exists { get; set; }
        public List<DenominationLineVm> Lines { get; set; } = new();
        public decimal Total { get; set; }
    }

    public class DenominationLineVm
    {
        public string Label { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal UnitValue { get; set; }
        public decimal LineTotal { get; set; }
    }

    public class DisbursementReportSectionVm
    {
        public long? DisbursementRecordId { get; set; }
        public DateTime? RecordDate { get; set; }

        public List<DisbursementVoucherVm> Vouchers { get; set; } = new();
        public List<DisbursementGroupVm> Groups { get; set; } = new();

        public decimal TotalReleased { get; set; }
        public decimal TotalReturned { get; set; }
        public decimal TotalNetDisbursement { get; set; }
    }

    public class DisbursementVoucherVm
    {
        public string VoucherNumber { get; set; } = string.Empty;
        public string Ministry { get; set; } = string.Empty;
        public string Payee { get; set; } = string.Empty;

        public List<DisbursementLineVm> Lines { get; set; } = new();

        public decimal VoucherTotalReleased { get; set; }
        public decimal VoucherTotalReturned { get; set; }
        public decimal VoucherNetAmount { get; set; }
    }

    public class DisbursementGroupVm
    {
        public string Ministry { get; set; } = string.Empty;
        public List<DisbursementLineVm> Lines { get; set; } = new();
        public decimal GroupTotal { get; set; }
        public string SortVoucherNumber { get; set; } = string.Empty;
    }

    public class DisbursementLineVm
    {
        public string VoucherNumber { get; set; } = string.Empty;
        public string Payee { get; set; } = string.Empty;
        public string Particular { get; set; } = string.Empty;

        public decimal AmountReleased { get; set; }
        public decimal CashReturned { get; set; }
        public decimal NetAmount { get; set; }

        public string SummaryLabel { get; set; } = string.Empty;
    }

    public class WeeklySummarySectionVm
    {
        public decimal CashReceiptsOrBlessings { get; set; }
        public decimal LessCashDisbursements { get; set; }
        public decimal NetCashBalance { get; set; }
    }

    public class MonthlyFinancialReportVm
    {
        public string MonthLabel { get; set; } = "";
        public string SelectedMonth { get; set; } = "";
        public List<MonthlyReportPageVm> Pages { get; set; } = new();
    }

    public class MonthlyReportPageVm
    {
        public DateTime ReportDate { get; set; }
        public bool HasReport { get; set; }
        public string EmptyMessage { get; set; } = "";
        public WeeklyFinancialReportVm Report { get; set; } = new();
    }
}