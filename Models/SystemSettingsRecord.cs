using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace acc_finance.Models
{
    [Table("SystemSettings")]
    public class SystemSettingsRecord : BaseModel
    {
        [PrimaryKey("id", false)]
        public int Id { get; set; }

        [Column("detailed_sheet_id")]
        public string DetailedSheetId { get; set; }

        [Column("financial_sheet_id")]
        public string FinancialSheetId { get; set; }
    }
}
