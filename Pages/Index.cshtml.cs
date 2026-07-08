using acc_finance.Models;
using acc_finance.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using System.Globalization;
using static Supabase.Postgrest.Constants;

namespace acc_finance.Pages
{
    [Authorize]
    public class IndexModel : PageModel
    {
        private readonly SupabaseService _supabase;
        private readonly IMemoryCache _cache;

        public IndexModel(SupabaseService supabase, IMemoryCache cache)
        {
            _supabase = supabase;
            _cache = cache;
        }

        public string ActiveSummaryTab { get; set; } = "Current Month";
        public string ActiveWalletTab { get; set; } = "";

        public string CurrentMonthLabel { get; set; } = "";
        public decimal TotalChurchFunds { get; set; }

        // 🚨 DYNAMIC WALLET LIST
        public List<FundWallet>
    DynamicWallets
        { get; set; } = new();

        public LatestActivityVm LatestGiving { get; set; } = new();
        public LatestActivityVm LatestDisbursement { get; set; } = new();

        public int DisplayYear { get; set; }
        public MonthlySummaryTableVm CurrentMonthSummary { get; set; } = new();
        public List<MonthlySummaryTableVm>
            AllYearSummaries
        { get; set; } = new();

        public string ErrorMessage { get; set; } = "";
        public bool IsDatabasePaused { get; set; } = false;
        public string TargetMonth { get; set; } = "";

        public async Task<IActionResult>
            OnGetAsync()
        {
            if (!User.Identity?.IsAuthenticated ?? true) return RedirectToPage("/Login");

            ActiveSummaryTab = HttpContext.Session.GetString("Dash_SummaryTab") ?? "Current Month";
            ActiveWalletTab = HttpContext.Session.GetString("Dash_WalletTab") ?? "";

            try
            {
                await _supabase.InitializeAsync(true);

                string dashCacheKey = "Dashboard_Live_Setup";
                if (!_cache.TryGetValue(dashCacheKey, out DashSetupCache setupCache))
                {
                    // 🚨 Fetch dynamic wallets alongside standard setup
                    var walletsTask = _supabase.Client.From<Wallet>().Filter("is_active", Operator.Equals, "true").Get();
                    var ministriesTask = _supabase.Client.From<Ministry>
                        ().Get();
                    var latestGivingTask = _supabase.Client.From<GivingRecord>()
                        .Select("*")
                        .Filter("is_closed", Operator.Equals, "true")
                        .Order("service_date", Ordering.Descending)
                        .Limit(1).Get();
                    var latestDisbTask = _supabase.Client.From<DisbursementRecord>
                        ().Select("*").Order("record_date", Ordering.Descending).Limit(1).Get();

                    await Task.WhenAll(walletsTask, ministriesTask, latestGivingTask, latestDisbTask);

                    var alg = latestGivingTask.Result.Models?.FirstOrDefault();
                    var ald = latestDisbTask.Result.Models?.FirstOrDefault();
                    var activeWallets = walletsTask.Result.Models ?? new List<Wallet>
                        ();
                    var activeMinistries = ministriesTask.Result.Models ?? new List<Ministry>
                        ();

                    DateTime targetDate = DateTime.Today;

                    if (alg != null && ald != null)
                        targetDate = alg.ServiceDate > ald.RecordDate ? alg.ServiceDate : ald.RecordDate;
                    else if (alg != null)
                        targetDate = alg.ServiceDate;
                    else if (ald != null)
                        targetDate = ald.RecordDate;

                    string targetMonthStr = targetDate.ToString("yyyy-MM");

                    var mfbResp = await _supabase.Client.From<MonthlyFundBalance>
                        ()
                        .Filter("report_month", Operator.LessThan, targetMonthStr)
                        .Order("report_month", Ordering.Descending)
                        .Get();

                    var recentBalances = mfbResp.Models ?? new List<MonthlyFundBalance>
                        ();

                    LatestActivityVm disbActivity = new();
                    if (ald != null)
                    {
                        var vResp = await _supabase.Client.From<Voucher>
                            ().Filter("disbursement_record_id", Operator.Equals, ald.Id.ToString()).Get();
                        var vouchers = vResp.Models ?? new List<Voucher>
                            ();
                        var vIds = vouchers.Select(v => (object)v.Id).ToList();

                        decimal netAmount = 0;
                        string impactStr = "General";

                        if (vIds.Any())
                        {
                            var iResp = await _supabase.Client.From<VoucherItem>
                                ().Filter("voucher_id", Operator.In, vIds).Get();
                            var items = iResp.Models ?? new List<VoucherItem>
                                ();
                            var validItems = items.Where(i => (i.Amount - i.AmountReturned) > 0).ToList();

                            netAmount = validItems.Sum(i => i.Amount - i.AmountReturned);
                            impactStr = validItems.GroupBy(i => string.IsNullOrWhiteSpace(i.FundSource) ? "General" : i.FundSource)
                            .OrderByDescending(g => g.Sum(i => i.Amount - i.AmountReturned))
                            .FirstOrDefault()?.Key ?? "General";
                        }

                        disbActivity = new LatestActivityVm
                        {
                            Date = ald.RecordDate,
                            Title = $"{vouchers.Count} Voucher(s)",
                            Amount = netAmount,
                            Impact = $"Deducted primarily from {impactStr} Fund",
                            Url = $"/Disbursements/Create?RecordDate={ald.RecordDate.ToString("yyyy-MM-dd")}"
                        };
                    }

                    setupCache = new DashSetupCache
                    {
                        Alg = alg,
                        Ald = ald,
                        RecentBalances = recentBalances,
                        TargetDate = targetDate,
                        LatestDisbActivity = disbActivity,
                        ActiveWallets = activeWallets,
                        ActiveMinistries = activeMinistries
                    };

                    // Cache for 15 seconds. Very fast invalidation without stressing the DB.
                    var setupOptions = new MemoryCacheEntryOptions()
                    .SetSize(5)
                    .SetAbsoluteExpiration(TimeSpan.FromSeconds(15));
                    _cache.Set(dashCacheKey, setupCache, setupOptions);
                }

                TargetMonth = setupCache.TargetDate.ToString("yyyy-MM");
                DateTime firstDay = new DateTime(setupCache.TargetDate.Year, setupCache.TargetDate.Month, 1);
                DateTime lastDay = firstDay.AddMonths(1).AddDays(-1);
                CurrentMonthLabel = firstDay.ToString("MMMM yyyy");

                var lastCheckpointMonth = setupCache.RecentBalances.FirstOrDefault()?.ReportMonth;

                // 🚨 Initialize Dictionary for Beginning Balances dynamically
                var begBalances = new Dictionary<string, decimal>
                    (StringComparer.OrdinalIgnoreCase);
                foreach (var w in setupCache.ActiveWallets) begBalances[w.Code] = 0m;

                DateTime gapStart = new DateTime(2000, 1, 1);

                if (!string.IsNullOrEmpty(lastCheckpointMonth))
                {
                    foreach (var w in setupCache.ActiveWallets)
                    {
                        begBalances[w.Code] = setupCache.RecentBalances.FirstOrDefault(b => b.ReportMonth == lastCheckpointMonth && string.Equals(b.FundName, w.Code, StringComparison.OrdinalIgnoreCase))?.EndingBalance ?? 0;
                    }
                    gapStart = DateTime.ParseExact(lastCheckpointMonth + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture).AddMonths(1);
                }

                DateTime gapEnd = firstDay.AddDays(-1);
                Dictionary<string, decimal>
                    gapInc = new(), gapExp = new();

                if (gapStart <= gapEnd)
                {
                    string gapCacheKey = $"GapMath_{gapStart:yyyyMMdd}_{gapEnd:yyyyMMdd}";
                    if (!_cache.TryGetValue(gapCacheKey, out GapMathCacheResult gapCache))
                    {
                        var gInc = await CalculateIncomeRangeAsync(gapStart, gapEnd, setupCache.ActiveWallets);
                        var gExp = await CalculateExpenseRangeAsync(gapStart, gapEnd, setupCache.ActiveWallets);

                        gapCache = new GapMathCacheResult { Inc = gInc, Exp = gExp };
                        var gapOptions = new MemoryCacheEntryOptions().SetSize(1).SetAbsoluteExpiration(TimeSpan.FromHours(12));
                        _cache.Set(gapCacheKey, gapCache, gapOptions);
                    }

                    gapInc = gapCache.Inc;
                    gapExp = gapCache.Exp;
                }

                var liveIncomeTask = CalculateIncomeRangeAsync(firstDay, lastDay, setupCache.ActiveWallets);
                var liveExpenseTask = CalculateExpenseRangeAsync(firstDay, lastDay, setupCache.ActiveWallets);

                await Task.WhenAll(liveIncomeTask, liveExpenseTask);

                var liveInc = await liveIncomeTask;
                var liveExp = await liveExpenseTask;

                // 🚨 Assemble the dynamic wallets
                foreach (var w in setupCache.ActiveWallets)
                {
                    decimal finalBalance = begBalances.GetValueOrDefault(w.Code, 0)
                    + gapInc.GetValueOrDefault(w.Code, 0)
                    - gapExp.GetValueOrDefault(w.Code, 0)
                    + liveInc.GetValueOrDefault(w.Code, 0)
                    - liveExp.GetValueOrDefault(w.Code, 0);

                    string custodian = w.CustodianType == "Person"
    ? w.CustodianPersonName ?? "Unknown"
    : setupCache.ActiveMinistries.FirstOrDefault(m => m.Id.ToString() == w.MinistryId?.ToString())?.Name ?? "Unknown Ministry";

                    DynamicWallets.Add(new FundWallet
                    {
                        Code = w.Code,
                        Name = w.DisplayName,
                        Custodian = custodian,
                        Theme = string.IsNullOrWhiteSpace(w.Theme) ? "primary" : w.Theme,
                        Icon = string.IsNullOrWhiteSpace(w.Icon) ? "bi-wallet2" : w.Icon,
                        EndBalance = finalBalance
                    });
                }

                TotalChurchFunds = DynamicWallets.Sum(w => w.CashOnHand);

                if (setupCache.Alg != null)
                {
                    LatestGiving = new LatestActivityVm
                    {
                        Date = setupCache.Alg.ServiceDate,
                        Title = $"Record: {setupCache.Alg.RecordCode}",
                        Amount = setupCache.Alg.GrandTotal,
                        Impact = "Distributed to General, Pledges & specific tags",
                        Url = $"/Giving/Entry?date={setupCache.Alg.ServiceDate.ToString("yyyy-MM-dd")}&RecordId={setupCache.Alg.Id}"
                    };
                }

                LatestDisbursement = setupCache.LatestDisbActivity;
                await BuildSummaryTablesAsync(setupCache.TargetDate.Year);

                return Page();
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("Task was canceled") || ex.Message.Contains("timeout") || ex.Message.Contains("503") || ex.Message.Contains("connection"))
                {
                    IsDatabasePaused = true;
                    ErrorMessage = "SYSTEM PAUSED: The cloud database is asleep. Please refresh in a few minutes.";
                }
                else { ErrorMessage = "System Error: " + ex.Message; }
                return Page();
            }
        }

        public IActionResult OnGetSaveState(string type, string? value)
        {
            string safeValue = value ?? "";
            if (type == "summary") HttpContext.Session.SetString("Dash_SummaryTab", safeValue);
            if (type == "wallet") HttpContext.Session.SetString("Dash_WalletTab", safeValue);
            return Content("");
        }

        private async Task BuildSummaryTablesAsync(int targetYear)
        {
            DisplayYear = targetYear;
            string summaryCacheKey = $"SummaryTable_{targetYear}";

            if (!_cache.TryGetValue(summaryCacheKey, out List<MonthlySummaryTableVm>
                cachedSummaries))
            {
                string yearStartStr = new DateTime(targetYear, 1, 1).ToString("yyyy-MM-dd");
                string yearEndStr = new DateTime(targetYear, 12, 31).ToString("yyyy-MM-dd");

                // 🚨 UPDATE THIS IN BuildSummaryTablesAsync()
                var yearGivingTask = _supabase.Client.From<GivingRecord>()
                    .Filter("service_date", Operator.GreaterThanOrEqual, yearStartStr)
                    .Filter("service_date", Operator.LessThanOrEqual, yearEndStr)
                    .Filter("is_closed", Operator.Equals, "true") 
                    .Get();
                var yearDisbTask = _supabase.Client.From<DisbursementRecord>
                    ().Filter("record_date", Operator.GreaterThanOrEqual, yearStartStr).Filter("record_date", Operator.LessThanOrEqual, yearEndStr).Get();

                await Task.WhenAll(yearGivingTask, yearDisbTask);

                var yearGivingRecords = yearGivingTask.Result.Models ?? new List<GivingRecord>
                    ();
                var yearDisbRecords = yearDisbTask.Result.Models ?? new List<DisbursementRecord>
                    ();

                var uniqueDates = yearGivingRecords.Select(g => g.ServiceDate.Date).Union(yearDisbRecords.Select(d => d.RecordDate.Date)).Distinct().OrderBy(d => d).ToList();
                var allDailySummaries = new List<DailySummaryVm>
                    ();

                foreach (var date in uniqueDates)
                {
                    decimal dayReceipts = yearGivingRecords.Where(g => g.ServiceDate.Date == date).Sum(g => g.GrandTotal);
                    decimal dayDisbursements = yearDisbRecords.Where(d => d.RecordDate.Date == date).Sum(d => d.TotalReleased - d.TotalReturned);
                    if (dayReceipts > 0 || dayDisbursements > 0) allDailySummaries.Add(new DailySummaryVm { Date = date, CashReceipts = dayReceipts, CashDisbursements = dayDisbursements });
                }

                cachedSummaries = new List<MonthlySummaryTableVm>
                    ();
                var monthlyGroups = allDailySummaries.GroupBy(ds => new { ds.Date.Year, ds.Date.Month }).OrderByDescending(g => g.Key.Year).ThenByDescending(g => g.Key.Month).ToList();

                foreach (var group in monthlyGroups)
                {
                    cachedSummaries.Add(new MonthlySummaryTableVm
                    {
                        MonthName = new DateTime(group.Key.Year, group.Key.Month, 1).ToString("MMMM").ToUpper(),
                        Year = group.Key.Year,
                        Quarter = (group.Key.Month - 1) / 3 + 1,
                        DailySummaries = group.OrderBy(ds => ds.Date).ToList()
                    });
                }

                var summaryOptions = new MemoryCacheEntryOptions().SetSize(5).SetAbsoluteExpiration(TimeSpan.FromMinutes(5));
                _cache.Set(summaryCacheKey, cachedSummaries, summaryOptions);
            }

            AllYearSummaries = cachedSummaries;
            if (AllYearSummaries.Any()) CurrentMonthSummary = AllYearSummaries.First();
        }

        // 🚨 Dynamic Income Calculator (Preserves original routing for Tithes/Offerings/Solomon)
        public async Task<Dictionary<string, decimal>> CalculateIncomeRangeAsync(DateTime start, DateTime end, List<Wallet> activeWallets)
        {
            string cacheKey = $"DashInc_{start:yyyyMMdd}_{end:yyyyMMdd}";
            if (_cache.TryGetValue(cacheKey, out Dictionary<string, decimal> cached)) return cached;

            var givingResp = await _supabase.Client.From<GivingRecord>()
                .Filter("service_date", Operator.GreaterThanOrEqual, start.ToString("yyyy-MM-dd"))
                .Filter("service_date", Operator.LessThanOrEqual, end.ToString("yyyy-MM-dd"))
                .Filter("is_closed", Operator.Equals, "true") // <-- ADD THIS FILTER
                .Get();
            var records = givingResp.Models ?? new List<GivingRecord>();
            var allEntries = new List<GivingEntry>();

            var recordIds = records.Select(r => (object)r.Id).ToList();
            if (recordIds.Any())
            {
                var entryResp = await _supabase.Client.From<GivingEntry>().Filter("giving_record_id", Operator.In, recordIds).Get();
                allEntries = entryResp.Models?.ToList() ?? new List<GivingEntry>();
            }

            var results = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

            // Apply core business logic routing first to ensure backward compatibility
            decimal coreGen = allEntries.Sum(e => e.Tithes + e.Offerings);
            decimal corePledges = allEntries.Sum(e => e.Solomon + e.Noah + e.Mission);

            results["General"] = coreGen;
            results["Pledges"] = corePledges;

            // Dynamically route all "Others" fields to their respective database wallet names!
            foreach (var entry in allEntries)
            {
                string fundKey = string.IsNullOrWhiteSpace(entry.OthersFund) ? "General" : entry.OthersFund;

                if (results.ContainsKey(fundKey))
                {
                    results[fundKey] += entry.Others;
                }
                else
                {
                    results[fundKey] = entry.Others;
                }
            }

            _cache.Set(cacheKey, results, new MemoryCacheEntryOptions().SetSize(1).SetAbsoluteExpiration(TimeSpan.FromMinutes(2)));
            return results;
        }

        // 🚨 Dynamic Expense Calculator
        public async Task<Dictionary<string, decimal>> CalculateExpenseRangeAsync(DateTime start, DateTime end, List<Wallet> activeWallets)
        {
            string cacheKey = $"DashExp_{start:yyyyMMdd}_{end:yyyyMMdd}";
            if (_cache.TryGetValue(cacheKey, out Dictionary<string, decimal> cached)) return cached;

            var disbResp = await _supabase.Client.From<DisbursementRecord>().Filter("record_date", Operator.GreaterThanOrEqual, start.ToString("yyyy-MM-dd")).Filter("record_date", Operator.LessThanOrEqual, end.ToString("yyyy-MM-dd")).Get();
            var records = disbResp.Models ?? new List<DisbursementRecord>();
            var allItems = new List<VoucherItem>();

            var recordIds = records.Select(r => (object)r.Id).ToList();
            if (recordIds.Any())
            {
                var vResp = await _supabase.Client.From<Voucher>().Filter("disbursement_record_id", Operator.In, recordIds).Get();
                var vouchers = vResp.Models ?? new List<Voucher>();

                var voucherIds = vouchers.Select(v => (object)v.Id).ToList();
                if (voucherIds.Any())
                {
                    var iResp = await _supabase.Client.From<VoucherItem>().Filter("voucher_id", Operator.In, voucherIds).Get();
                    allItems = iResp.Models?.ToList() ?? new List<VoucherItem>();
                }
            }

            var results = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in allItems)
            {
                decimal netExp = item.Amount - item.AmountReturned;
                if (netExp <= 0) continue;

                string fundKey = string.IsNullOrWhiteSpace(item.FundSource) ? "General" : item.FundSource;

                if (results.ContainsKey(fundKey))
                {
                    results[fundKey] += netExp;
                }
                else
                {
                    results[fundKey] = netExp;
                }
            }

            _cache.Set(cacheKey, results, new MemoryCacheEntryOptions().SetSize(1).SetAbsoluteExpiration(TimeSpan.FromMinutes(2)));
            return results;
        }

        private class GapMathCacheResult
        {
            public Dictionary<string, decimal> Inc { get; set; } = new();
            public Dictionary<string, decimal> Exp { get; set; } = new();
        }

        private class DashSetupCache
        {
            public GivingRecord? Alg { get; set; }
            public DisbursementRecord? Ald { get; set; }
            public List<MonthlyFundBalance> RecentBalances { get; set; } = new();
            public DateTime TargetDate { get; set; }
            public LatestActivityVm LatestDisbActivity { get; set; } = new();
            public List<Wallet> ActiveWallets { get; set; } = new();
            public List<Ministry> ActiveMinistries { get; set; } = new();
        }
    }

    public class FundWallet
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string Custodian { get; set; } = "";
        public string Theme { get; set; } = "primary";
        public string Icon { get; set; } = "";
        public decimal EndBalance { get; set; }

        public decimal CashOnHand => EndBalance;
    }

    public class LatestActivityVm
    {
        public DateTime Date { get; set; }
        public string Title { get; set; } = "";
        public decimal Amount { get; set; }
        public string Impact { get; set; } = "";
        public string Url { get; set; } = "#";
    }

    public class DailySummaryVm
    {
        public DateTime Date { get; set; }
        public decimal CashReceipts { get; set; }
        public decimal CashDisbursements { get; set; }
        public decimal NetBalance => CashReceipts - CashDisbursements;
    }

    public class MonthlySummaryTableVm
    {
        public string MonthName { get; set; } = "";
        public int Year { get; set; }
        public List<DailySummaryVm> DailySummaries { get; set; } = new List<DailySummaryVm>();
        public int Quarter { get; set; }
    }
}
