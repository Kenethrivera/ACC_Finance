using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace acc_finance.Models
{
    [Table("disbursement_records")]
    public class DisbursementRecord : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("giving_record_id")]
        public long? GivingRecordId { get; set; }

        [Column("record_date")]
        public DateTime RecordDate { get; set; }

        [Column("total_blessing")]
        public decimal TotalBlessing { get; set; }

        [Column("total_released")]
        public decimal TotalReleased { get; set; }

        [Column("total_returned")]
        public decimal TotalReturned { get; set; }

        [Column("is_closed")]
        public bool IsClosed { get; set; }

        [Column("notes")]
        public string? Notes { get; set; }

        [Column("created_by")]
        public string? CreatedBy { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("updated_at")]
        public DateTime UpdatedAt { get; set; }
    }
}