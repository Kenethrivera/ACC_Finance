using Supabase.Postgrest.Models;
using Supabase.Postgrest.Attributes;

namespace acc_finance.Models
{
    [Table("giving_denominations")]
    public class GivingDenomination : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("giving_record_id")]
        public long GivingRecordId { get; set; }

        [Column("qty_1000")]
        public int Qty1000 { get; set; }

        [Column("qty_500")]
        public int Qty500 { get; set; }

        [Column("qty_200")]
        public int Qty200 { get; set; }

        [Column("qty_100")]
        public int Qty100 { get; set; }

        [Column("qty_50")]
        public int Qty50 { get; set; }

        [Column("qty_20_coin")]
        public int Qty20Coin { get; set; }

        [Column("qty_20_paper")]
        public int Qty20Paper { get; set; }

        [Column("qty_10")]
        public int Qty10 { get; set; }

        [Column("qty_5")]
        public int Qty5 { get; set; }

        [Column("qty_1")]
        public int Qty1 { get; set; }

        [Column("qty_25_cent")]
        public int Qty25Cent { get; set; }

        [Column("qty_10_cent")]
        public int Qty10Cent { get; set; }

        [Column("qty_5_cent")]
        public int Qty5Cent { get; set; }

        [Column("qty_1_cent")]
        public int Qty1Cent { get; set; }

        [Column("total")]
        public decimal Total { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("updated_at")]
        public DateTime UpdatedAt { get; set; }
    }
}