using Supabase.Postgrest.Models;
using Supabase.Postgrest.Attributes;

namespace acc_finance.Models
{
    [Table("giving_records")]
    public class GivingRecord : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("record_code")]
        public string RecordCode { get; set; } = string.Empty;

        [Column("service_date")]
        public DateTime ServiceDate { get; set; }

        [Column("notes")]
        public string? Notes { get; set; }

        [Column("is_closed")]
        public bool IsClosed { get; set; }

        [Column("grand_total")]
        public decimal GrandTotal { get; set; }

        [Column("created_by")]
        public string? CreatedBy { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("total_tithes")]
        public decimal TotalTithes { get; set; }

        [Column("total_offerings")]
        public decimal TotalOfferings { get; set; }

        [Column("total_solomon")]
        public decimal TotalSolomon { get; set; }

        [Column("total_noah")]
        public decimal TotalNoah { get; set; }

        [Column("total_mission")]
        public decimal TotalMission { get; set; }

        [Column("total_others")]
        public decimal TotalOthers { get; set; }

    }
}
