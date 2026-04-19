using Supabase;
using Supabase.Postgrest;

namespace acc_finance.Services
{
    public class SupabaseService
    {
        public Supabase.Client Client { get; private set; } = null!;
        private readonly IConfiguration _configuration;

        public SupabaseService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public async Task InitializeAsync(bool userServiceRole = false)
        {
            var url = _configuration["Supabase:Url"]
                ?? throw new Exception("Supabase URL not found");

            string key;

            if (userServiceRole)
            {
                key = _configuration["Supabase:ServiceRoleKey"];
                if (string.IsNullOrEmpty(key))
                {
                    throw new Exception("CRITICAL: Service Role Key is not provided or configured");
                }
            }
            else
            {
                key = _configuration["Supabase:Key"];
            }

            if (string.IsNullOrEmpty(key))
                throw new Exception("Supabase Key (Anon) not found");

            var options = new SupabaseOptions
            {
                AutoConnectRealtime = false
            };

            Client = new Supabase.Client(url, key, options);
            await Client.InitializeAsync();
        }
    }
}
