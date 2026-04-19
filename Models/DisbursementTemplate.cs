using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace acc_finance.Models
{
    [Table("disbursement_templates")]
    public class DisbursementTemplate : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("template_name")]
        public string TemplateName { get; set; } = "";

        [Column("ministry")]
        public string Ministry { get; set; } = "";

        [Column("payee")]
        public string Payee { get; set; } = "";

        [Column("is_active")]
        public bool IsActive { get; set; }

        [Column("created_by")]
        public string? CreatedBy { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("updated_at")]
        public DateTime UpdatedAt { get; set; }

        [Column("auto_apply")]
        public bool AutoApply { get; set; }

        [Column("recurrence_type")]
        public string? RecurrenceType { get; set; }

        [Column("week_of_month")]
        public int? WeekOfMonth { get; set; }
    }
}