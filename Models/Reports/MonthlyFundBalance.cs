using Supabase.Postgrest.Models;
using Supabase.Postgrest.Attributes;

namespace acc_finance.Models
{
    [Table("monthly_fund_balances")]
    public class MonthlyFundBalance : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("report_month")]
        public string ReportMonth { get; set; } = string.Empty;

        [Column("fund_name")]
        public string FundName { get; set; } = string.Empty;

        [Column("custodian")]
        public string Custodian { get; set; } = string.Empty;

        [Column("ending_balance")]
        public decimal EndingBalance { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
    }
}