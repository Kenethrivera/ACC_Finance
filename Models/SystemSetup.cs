using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace acc_finance.Models

{
    public class SystemSetup
    {
        [Table("export_logs")]
        public class ExportLogRecord : BaseModel
        {
            [PrimaryKey("report_month", false)]
            public string ReportMonth { get; set; }

            [Column("detailed_fingerprint")]
            public string DetailedFingerprint { get; set; }

            [Column("financial_fingerprint")]
            public string FinancialFingerprint { get; set; }
        }
    }
}
