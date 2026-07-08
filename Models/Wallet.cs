using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;
using System;

namespace acc_finance.Models
{
    [Table("wallets")]
    public class Wallet : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("code")]
        public string Code { get; set; } = "";

        [Column("display_name")]
        public string DisplayName { get; set; } = "";

        // "Person" or "Ministry"
        [Column("custodian_type")]
        public string CustodianType { get; set; } = "Person";

        [Column("custodian_person_name")]
        public string? CustodianPersonName { get; set; }

        [Column("ministry_id")]
        public long? MinistryId { get; set; }

        [Column("theme")]
        public string Theme { get; set; }

        [Column("icon")]
        public string Icon { get; set; }

        [Column("is_active")]
        public bool Is_Active { get; set; } = true;

        [Column("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}