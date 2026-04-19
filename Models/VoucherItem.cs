using Supabase.Postgrest.Models;
using Supabase.Postgrest.Attributes;

namespace acc_finance.Models
{
    [Table("voucher_items")]
    public class VoucherItem : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("voucher_id")]
        public long VoucherId { get; set; }

        [Column("particular")]
        public string Particular { get; set; } = string.Empty;

        [Column("amount")]
        public decimal Amount { get; set; }

        [Column("amount_returned")]
        public decimal AmountReturned { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
    }
}