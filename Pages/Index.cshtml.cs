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

        public FundWallet GeneralFund { get; set; } = new();
        public FundWallet PledgesFund { get; set; } = new();
        public FundWallet ConstructionFund { get; set; } = new();
        public FundWallet PWFund { get; set; } = new();

        public LatestActivityVm LatestGiving { get; set; } = new();
        public LatestActivityVm LatestDisbursement { get; set; } = new();

        public int DisplayYear { get; set; }
        public MonthlySummaryTableVm CurrentMonthSummary { get; set; } = new();
        public List<MonthlySummaryTableVm> AllYearSummaries { get; set; } = new();

        public string ErrorMessage { get; set; } = "";
        public bool IsDatabasePaused { get; set; } = false;
        public string TargetMonth { get; set; } = "";

        public async Task<IActionResult> OnGetAsync()
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
                    var latestGivingTask = _supabase.Client.From<GivingRecord>().Select("*").Order("service_date", Ordering.Descending).Limit(1).Get();
                    var latestDisbTask = _supabase.Client.From<DisbursementRecord>().Select("*").Order("record_date", Ordering.Descending).Limit(1).Get();

                    // REMOVED LOAN TASK HERE
                    await Task.WhenAll(latestGivingTask, latestDisbTask);

                    var alg = latestGivingTask.Result.Models?.FirstOrDefault();
                    var ald = latestDisbTask.Result.Models?.FirstOrDefault();

                    DateTime targetDate = DateTime.Today;

                    if (alg != null && ald != null)
                        targetDate = alg.ServiceDate > ald.RecordDate ? alg.ServiceDate : ald.RecordDate;
                    else if (alg != null)
                        targetDate = alg.ServiceDate;
                    else if (ald != null)
                        targetDate = ald.RecordDate;

                    string targetMonthStr = targetDate.ToString("yyyy-MM");

                    var mfbResp = await _supabase.Client.From<MonthlyFundBalance>()
                        .Filter("report_month", Operator.LessThan, targetMonthStr)
                        .Order("report_month", Ordering.Descending)
                        .Limit(10)
                        .Get();

                    var recentBalances = mfbResp.Models ?? new List<MonthlyFundBalance>();

                    LatestActivityVm disbActivity = new();
                    if (ald != null)
                    {
                        var vResp = await _supabase.Client.From<Voucher>().Filter("disbursement_record_id", Operator.Equals, ald.Id.ToString()).Get();
                        var vouchers = vResp.Models ?? new List<Voucher>();
                        var vIds = vouchers.Select(v => (object)v.Id).ToList();

                        decimal netAmount = 0;
                        string impactStr = "General";

                        if (vIds.Any())
                        {
                            var iResp = await _supabase.Client.From<VoucherItem>().Filter("voucher_id", Operator.In, vIds).Get();
                            var items = iResp.Models ?? new List<VoucherItem>();
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
                            Impact = $"Deducted primarily from {impactStr} Fund"
                        };
                    }

                    setupCache = new DashSetupCache
                    {
                        Alg = alg,
                        Ald = ald,
                        RecentBalances = recentBalances,
                        TargetDate = targetDate,
                        LatestDisbActivity = disbActivity
                    };

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
                decimal begGen = 0, begPledges = 0, begConst = 0, begPW = 0;
                DateTime gapStart = new DateTime(2000, 1, 1);

                if (!string.IsNullOrEmpty(lastCheckpointMonth))
                {
                    begGen = setupCache.RecentBalances.FirstOrDefault(b => b.ReportMonth == lastCheckpointMonth && b.FundName == "General")?.EndingBalance ?? 0;
                    begPledges = setupCache.RecentBalances.FirstOrDefault(b => b.ReportMonth == lastCheckpointMonth && b.FundName == "Pledges")?.EndingBalance ?? 0;
                    begConst = setupCache.RecentBalances.FirstOrDefault(b => b.ReportMonth == lastCheckpointMonth && b.FundName == "Construction")?.EndingBalance ?? 0;
                    begPW = setupCache.RecentBalances.FirstOrDefault(b => b.ReportMonth == lastCheckpointMonth && b.FundName == "Praise & Worship")?.EndingBalance ?? 0;
                    gapStart = DateTime.ParseExact(lastCheckpointMonth + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture).AddMonths(1);
                }

                DateTime gapEnd = firstDay.AddDays(-1);

                decimal gapIncGen = 0, gapIncPledges = 0, gapIncConst = 0, gapIncPW = 0;
                decimal gapExpGen = 0, gapExpPledges = 0, gapExpConst = 0, gapExpPW = 0;

                if (gapStart <= gapEnd)
                {
                    string gapCacheKey = $"GapMath_{gapStart:yyyyMMdd}_{gapEnd:yyyyMMdd}";
                    if (!_cache.TryGetValue(gapCacheKey, out GapMathCacheResult gapCache))
                    {
                        var (gIncG, gIncP, gIncC, gIncPW) = await CalculateIncomeRangeAsync(gapStart, gapEnd);
                        var (gExpG, gExpP, gExpC, gExpPW) = await CalculateExpenseRangeAsync(gapStart, gapEnd);

                        gapCache = new GapMathCacheResult { Inc = new[] { gIncG, gIncP, gIncC, gIncPW }, Exp = new[] { gExpG, gExpP, gExpC, gExpPW } };
                        var gapOptions = new MemoryCacheEntryOptions()
                            .SetSize(1)
                            .SetAbsoluteExpiration(TimeSpan.FromHours(12));
                        _cache.Set(gapCacheKey, gapCache, gapOptions);
                    }

                    gapIncGen = gapCache.Inc[0]; gapIncPledges = gapCache.Inc[1]; gapIncConst = gapCache.Inc[2]; gapIncPW = gapCache.Inc[3];
                    gapExpGen = gapCache.Exp[0]; gapExpPledges = gapCache.Exp[1]; gapExpConst = gapCache.Exp[2]; gapExpPW = gapCache.Exp[3];
                }

                begGen += (gapIncGen - gapExpGen);
                begPledges += (gapIncPledges - gapExpPledges);
                begConst += (gapIncConst - gapExpConst);
                begPW += (gapIncPW - gapExpPW);

                var liveIncomeTask = CalculateIncomeRangeAsync(firstDay, lastDay);
                var liveExpenseTask = CalculateExpenseRangeAsync(firstDay, lastDay);

                await Task.WhenAll(liveIncomeTask, liveExpenseTask);

                var (incGen, incPledges, incConst, incPW) = await liveIncomeTask;
                var (expGen, expPledges, expConst, expPW) = await liveExpenseTask;

                // REMOVED ALL LOAN LENT/BORROWED CALCULATIONS HERE

                GeneralFund = new FundWallet { Name = "General Fund", Custodian = "Sis. Cora", Theme = "primary", Icon = "bi-wallet2", EndBalance = begGen + incGen - expGen };
                PledgesFund = new FundWallet { Name = "Pledges", Custodian = "Sis. Cora", Theme = "warning", Icon = "bi-journal-check", EndBalance = begPledges + incPledges - expPledges };
                ConstructionFund = new FundWallet { Name = "Construction", Custodian = "Ptra Es", Theme = "info", Icon = "bi-tools", EndBalance = begConst + incConst - expConst };
                PWFund = new FundWallet { Name = "Praise & Worship", Custodian = "P/W", Theme = "success", Icon = "bi-music-note-beamed", EndBalance = begPW + incPW - expPW };

                TotalChurchFunds = GeneralFund.CashOnHand + PledgesFund.CashOnHand + ConstructionFund.CashOnHand + PWFund.CashOnHand;

                if (setupCache.Alg != null) LatestGiving = new LatestActivityVm { Date = setupCache.Alg.ServiceDate, Title = $"Record: {setupCache.Alg.RecordCode}", Amount = setupCache.Alg.GrandTotal, Impact = "Distributed to General, Pledges & specific tags" };
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

            if (!_cache.TryGetValue(summaryCacheKey, out List<MonthlySummaryTableVm> cachedSummaries))
            {
                string yearStartStr = new DateTime(targetYear, 1, 1).ToString("yyyy-MM-dd");
                string yearEndStr = new DateTime(targetYear, 12, 31).ToString("yyyy-MM-dd");

                var yearGivingTask = _supabase.Client.From<GivingRecord>().Filter("service_date", Operator.GreaterThanOrEqual, yearStartStr).Filter("service_date", Operator.LessThanOrEqual, yearEndStr).Get();
                var yearDisbTask = _supabase.Client.From<DisbursementRecord>().Filter("record_date", Operator.GreaterThanOrEqual, yearStartStr).Filter("record_date", Operator.LessThanOrEqual, yearEndStr).Get();

                await Task.WhenAll(yearGivingTask, yearDisbTask);

                var yearGivingRecords = yearGivingTask.Result.Models ?? new List<GivingRecord>();
                var yearDisbRecords = yearDisbTask.Result.Models ?? new List<DisbursementRecord>();

                var uniqueDates = yearGivingRecords.Select(g => g.ServiceDate.Date).Union(yearDisbRecords.Select(d => d.RecordDate.Date)).Distinct().OrderBy(d => d).ToList();
                var allDailySummaries = new List<DailySummaryVm>();

                foreach (var date in uniqueDates)
                {
                    decimal dayReceipts = yearGivingRecords.Where(g => g.ServiceDate.Date == date).Sum(g => g.GrandTotal);
                    decimal dayDisbursements = yearDisbRecords.Where(d => d.RecordDate.Date == date).Sum(d => d.TotalReleased - d.TotalReturned);
                    if (dayReceipts > 0 || dayDisbursements > 0) allDailySummaries.Add(new DailySummaryVm { Date = date, CashReceipts = dayReceipts, CashDisbursements = dayDisbursements });
                }

                cachedSummaries = new List<MonthlySummaryTableVm>();
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

        public async Task<(decimal gen, decimal pledges, decimal cons, decimal pw)> CalculateIncomeRangeAsync(DateTime start, DateTime end)
        {
            string cacheKey = $"DashInc_{start:yyyyMMdd}_{end:yyyyMMdd}";
            if (_cache.TryGetValue(cacheKey, out Tuple<decimal, decimal, decimal, decimal> cached)) return (cached.Item1, cached.Item2, cached.Item3, cached.Item4);

            var givingResp = await _supabase.Client.From<GivingRecord>().Filter("service_date", Operator.GreaterThanOrEqual, start.ToString("yyyy-MM-dd")).Filter("service_date", Operator.LessThanOrEqual, end.ToString("yyyy-MM-dd")).Get();
            var records = givingResp.Models ?? new List<GivingRecord>();
            var allEntries = new List<GivingEntry>();

            var recordIds = records.Select(r => (object)r.Id).ToList();
            if (recordIds.Any())
            {
                var entryResp = await _supabase.Client.From<GivingEntry>().Filter("giving_record_id", Operator.In, recordIds).Get();
                allEntries = entryResp.Models?.ToList() ?? new List<GivingEntry>();
            }

            decimal pledges = allEntries.Sum(e => e.Solomon + e.Noah + e.Mission) + allEntries.Where(e => e.OthersFund == "Pledges").Sum(e => e.Others);
            decimal gen = allEntries.Sum(e => e.Tithes + e.Offerings) + allEntries.Where(e => e.OthersFund == "General" || string.IsNullOrWhiteSpace(e.OthersFund)).Sum(e => e.Others);
            decimal cons = allEntries.Where(e => e.OthersFund == "Construction").Sum(e => e.Others);
            decimal pw = allEntries.Where(e => e.OthersFund == "Praise & Worship").Sum(e => e.Others);

            var result = (gen, pledges, cons, pw);
            _cache.Set(cacheKey, new Tuple<decimal, decimal, decimal, decimal>(gen, pledges, cons, pw), new MemoryCacheEntryOptions().SetSize(1).SetAbsoluteExpiration(TimeSpan.FromMinutes(2)));
            return result;
        }

        public async Task<(decimal gen, decimal pledges, decimal cons, decimal pw)> CalculateExpenseRangeAsync(DateTime start, DateTime end)
        {
            string cacheKey = $"DashExp_{start:yyyyMMdd}_{end:yyyyMMdd}";
            if (_cache.TryGetValue(cacheKey, out Tuple<decimal, decimal, decimal, decimal> cached)) return (cached.Item1, cached.Item2, cached.Item3, cached.Item4);

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

            decimal gen = 0, pledges = 0, cons = 0, pw = 0;
            foreach (var item in allItems)
            {
                decimal netExp = item.Amount - item.AmountReturned;
                if (netExp <= 0) continue;

                string fund = item.FundSource ?? "General";
                if (fund == "Construction") cons += netExp;
                else if (fund == "Praise & Worship") pw += netExp;
                else if (fund == "Pledges") pledges += netExp;
                else gen += netExp;
            }

            var result = (gen, pledges, cons, pw);
            _cache.Set(cacheKey, new Tuple<decimal, decimal, decimal, decimal>(gen, pledges, cons, pw), new MemoryCacheEntryOptions().SetSize(1).SetAbsoluteExpiration(TimeSpan.FromMinutes(2)));
            return result;
        }

        private class GapMathCacheResult { public decimal[] Inc { get; set; } public decimal[] Exp { get; set; } }
        private class DashSetupCache
        {
            public GivingRecord? Alg { get; set; }
            public DisbursementRecord? Ald { get; set; }
            public List<MonthlyFundBalance> RecentBalances { get; set; } = new();
            public DateTime TargetDate { get; set; }
            public LatestActivityVm LatestDisbActivity { get; set; } = new();
        }
    }

    public class FundWallet
    {
        public string Name { get; set; } = "";
        public string Custodian { get; set; } = "";
        public string Theme { get; set; } = "primary";
        public string Icon { get; set; } = "";
        public decimal EndBalance { get; set; }

        // SIMPLIFIED: Cash on hand is now just the true End Balance.
        public decimal CashOnHand => EndBalance;
    }

    public class LatestActivityVm
    {
        public DateTime Date { get; set; }
        public string Title { get; set; } = "";
        public decimal Amount { get; set; }
        public string Impact { get; set; } = "";
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