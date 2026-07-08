using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;
using System;

namespace acc_finance.Models
{
    [Table("ministries")]
    public class Ministry : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("name")]
        public string Name { get; set; } = "";

        [Column("leader_name")]
        public string? LeaderName { get; set; }

        [Column("contact_info")]
        public string? ContactInfo { get; set; }

        [Column("is_active")]
        public bool Is_Active { get; set; } = true;

        [Column("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}