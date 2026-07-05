using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace acc_finance.Models
{
    [Table("disbursement_template_items")]
    public class DisbursementTemplateItem : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("template_id")]
        public long TemplateId { get; set; }

        [Column("line_no")]
        public int LineNo { get; set; }

        [Column("particular")]
        public string Particular { get; set; } = "";

        [Column("amount")]
        public decimal Amount { get; set; }

        [Column("fund_source")]
        public string FundSource { get; set; } = "General";
    }
}