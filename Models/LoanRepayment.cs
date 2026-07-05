using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace acc_finance.Models
{
    [Table("loan_repayments")]
    public class LoanRepayment : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("loan_id")]
        public long LoanId { get; set; }

        [Column("repayment_date")]
        public DateTime RepaymentDate { get; set; }

        [Column("amount")]
        public decimal Amount { get; set; }

        [Column("notes")]
        public string Notes { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
    }
}