using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace acc_finance.Models
{
    [Table("monthly_pledge_breakdowns")]
    public class MonthlyPledgeBreakdown : BaseModel
    {
        [PrimaryKey("id", false)]
        public int Id { get; set; }

        [Column("report_month")]
        public string ReportMonth { get; set; } = "";

        [Column("solomon_balance")]
        public decimal SolomonBalance { get; set; }

        [Column("noah_balance")]
        public decimal NoahBalance { get; set; }

        [Column("mission_balance")]
        public decimal MissionBalance { get; set; }

        [Column("others_balance")]
        public decimal OthersBalance { get; set; }
    }
}