using Supabase.Postgrest.Models;
using Supabase.Postgrest.Attributes;

namespace acc_finance.Models
{
    [Table("vouchers")]
    public class Voucher : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("disbursement_record_id")]
        public long DisbursementRecordId { get; set; }

        [Column("voucher_number")]
        public string VoucherNumber { get; set; } = string.Empty;

        [Column("ministry")]
        public string Ministry { get; set; } = string.Empty;

        [Column("payee")]
        public string Payee { get; set; } = string.Empty;

        [Column("amount_released")]
        public decimal AmountReleased { get; set; }

        [Column("amount_returned")]
        public decimal AmountReturned { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("updated_at")]
        public DateTime UpdatedAt { get; set; }

       
    }
}