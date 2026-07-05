using Microsoft.AspNetCore.Mvc.RazorPages;
using acc_finance.Models;
using acc_finance.Services;
using Microsoft.AspNetCore.Authorization;

namespace acc_finance.Pages.DebtTracker
{
    [Authorize]
    public class IndexModel : PageModel
    {
        private readonly SupabaseService _supabase;

        public IndexModel(SupabaseService supabase)
        {
            _supabase = supabase;
        }

        public List<InterFundLoan> AllLoans { get; set; } = new();
        public decimal TotalActiveDebt { get; set; }
        public decimal TotalRepaid { get; set; }

        public async Task OnGetAsync()
        {
            await _supabase.InitializeAsync(true);

            // Fetch all loans, newest first
            var response = await _supabase.Client.From<InterFundLoan>().Get();
            AllLoans = response.Models?.OrderByDescending(l => l.RecordDate).ToList() ?? new List<InterFundLoan>();

            // Calculate summaries
            TotalActiveDebt = AllLoans.Where(l => l.Status == "Active").Sum(l => l.RemainingBalance);
            TotalRepaid = AllLoans.Sum(l => l.OriginalAmount - l.RemainingBalance);
        }
    }
}