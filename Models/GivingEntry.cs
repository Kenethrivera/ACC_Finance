using Supabase.Postgrest.Models;
using Supabase.Postgrest.Attributes;

namespace acc_finance.Models
{
    [Table("giving_entries")]
    public class GivingEntry : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("giving_record_id")]
        public long GivingRecordId { get; set; }

        [Column("member_id")]
        public long? MemberId { get; set; }

        [Column("entry_name")]
        public string? EntryName { get; set; }

        [Column("tithes")]
        public decimal Tithes { get; set; }

        [Column("offerings")]
        public decimal Offerings { get; set; }

        [Column("solomon")]
        public decimal Solomon { get; set; }

        [Column("noah")]
        public decimal Noah { get; set; }

        [Column("mission")]
        public decimal Mission { get; set; }

        [Column("others")]
        public decimal Others { get; set; }

        [Column("total")]
        public decimal Total { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("updated_at")]
        public DateTime UpdatedAt { get; set; }
    }
}
