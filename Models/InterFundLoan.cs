using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace acc_finance.Models
{
    [Table("inter_fund_loans")]
    public class InterFundLoan : BaseModel
    {
        [PrimaryKey("id", false)]
        public int Id { get; set; }

        [Column("borrower_fund")]
        public string BorrowerFund { get; set; } = "";

        [Column("lender_fund")]
        public string LenderFund { get; set; } = "";

        [Column("disbursement_record_id")]
        public long? DisbursementRecordId { get; set; }

        [Column("record_date")]
        public DateTime RecordDate { get; set; }

        [Column("original_amount")]
        public decimal OriginalAmount { get; set; }

        [Column("remaining_balance")]
        public decimal RemainingBalance { get; set; }

        [Column("status")]
        public string Status { get; set; } = "Active";

        [Column("notes")]
        public string Notes { get; set; } = "";
    }
}