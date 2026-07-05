using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory; 
using acc_finance.Models;
using acc_finance.Services;
using Microsoft.AspNetCore.Authorization;

namespace acc_finance.Pages.DebtTracker
{
    [Authorize(Roles = "Admin")]
    public class PayModel : PageModel
    {
        private readonly SupabaseService _supabase;
        private readonly IMemoryCache _cache; 

        public PayModel(SupabaseService supabase, IMemoryCache cache)
        {
            _supabase = supabase;
            _cache = cache;
        }

        [BindProperty(SupportsGet = true)]
        public long LoanId { get; set; }

        public InterFundLoan TargetLoan { get; set; } = new();

        [BindProperty]
        public decimal PaymentAmount { get; set; }

        [BindProperty]
        public string PaymentNotes { get; set; } = "";

        public string ErrorMessage { get; set; } = "";

        public async Task<IActionResult> OnGetAsync()
        {
            await _supabase.InitializeAsync(true);

            var response = await _supabase.Client.From<InterFundLoan>().Where(l => l.Id == LoanId).Get();
            TargetLoan = response.Models.FirstOrDefault();

            if (TargetLoan == null || TargetLoan.Status == "Paid")
                return RedirectToPage("/Index");

            PaymentAmount = TargetLoan.RemainingBalance;

            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            await _supabase.InitializeAsync(true);

            var response = await _supabase.Client.From<InterFundLoan>().Where(l => l.Id == LoanId).Get();
            TargetLoan = response.Models.FirstOrDefault();

            if (TargetLoan == null)
                return RedirectToPage("/Index");

            if (PaymentAmount <= 0)
            {
                ErrorMessage = "Payment amount must be greater than zero.";
                return Page();
            }

            if (PaymentAmount > TargetLoan.RemainingBalance)
            {
                ErrorMessage = "Payment cannot exceed the remaining balance. Check your math!";
                return Page();
            }

            // 1. UPDATE THE MASTER LOAN RECORD
            TargetLoan.RemainingBalance -= PaymentAmount;

            if (TargetLoan.RemainingBalance <= 0)
            {
                TargetLoan.RemainingBalance = 0;
                TargetLoan.Status = "Paid";
            }

            string formattedNote = string.IsNullOrWhiteSpace(PaymentNotes) ? "Standard repayment." : PaymentNotes;

            // Still keep a quick log in the notes for easy viewing
            TargetLoan.Notes += $"\n[Paid ₱{PaymentAmount:N2} on {DateTime.Now:MMM dd}]";

            await _supabase.Client.From<InterFundLoan>().Update(TargetLoan);

            // 2. INSERT INTO YOUR NEW LOAN_REPAYMENTS TABLE
            var repaymentAudit = new LoanRepayment
            {
                LoanId = this.LoanId,
                RepaymentDate = DateTime.Now,
                Amount = PaymentAmount,
                Notes = formattedNote,
                CreatedAt = DateTime.UtcNow
            };

            await _supabase.Client.From<LoanRepayment>().Insert(repaymentAudit);

            // --------------------------------------------------
            // 🚨 3. COMPREHENSIVE CACHE INVALIDATION 🚨
            // --------------------------------------------------
            string loanMonth = TargetLoan.RecordDate.ToString("yyyy-MM");
            string currentMonth = DateTime.Today.ToString("yyyy-MM");

            // Clear Global Dashboard Caches
            _cache.Remove("Dashboard_Live_Setup");
            _cache.Remove($"DashBalances_ExtraCash_{DateTime.Today:yyyyMMdd}");

            // Clear Wallet Data for BOTH the loan's origin month and the current month
            _cache.Remove($"WalletMonthData_{loanMonth}");
            _cache.Remove($"WalletMonthData_{currentMonth}");

            // Clear System Reports & Summaries
            _cache.Remove($"SystemReport_{loanMonth}");
            _cache.Remove($"SystemReport_{currentMonth}");
            _cache.Remove($"SummaryTable_{TargetLoan.RecordDate.Year}");
            _cache.Remove($"SummaryTable_{DateTime.Today.Year}");
            // --------------------------------------------------

            // 4. FIX THE ROUTING BUG: Send them back to the month the loan was created in!
            string returnMonth = TargetLoan.RecordDate.ToString("yyyy-MM");

            return RedirectToPage("/WalletDetails", new { fund = TargetLoan.BorrowerFund, month = returnMonth });
        }
    }
}