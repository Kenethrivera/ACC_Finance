using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace acc_finance.Models
{
    [Table("members")]
    public class Member : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("name")]
        public string Name { get; set; } = string.Empty;

        [Column("is_active")]
        public bool Is_Active { get; set; }

        [Column("created_At")]
        public DateTime CreatedAt { get; set; }



    }
}
