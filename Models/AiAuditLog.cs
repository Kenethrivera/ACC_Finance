using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;
using System;

namespace acc_finance.Models
{
    [Table("ai_audit_logs")]
    public class AiAuditLog : BaseModel
    {
        [PrimaryKey("id", false)]
        public string Id { get; set; }

        [Column("report_month")]
        public string ReportMonth { get; set; }

        [Column("data_fingerprint")]
        public string DataFingerprint { get; set; }

        [Column("ai_html_summary")]
        public string AiHtmlSummary { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
    }
}