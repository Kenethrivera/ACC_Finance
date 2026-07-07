using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using acc_finance.Models;
using acc_finance.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System;
using System.Globalization;

namespace acc_finance.Pages.RB
{
    [Authorize]
    public class IndexModel : PageModel
    {
        private readonly SupabaseService _supabase;
        private readonly IMemoryCache _cache;

        public const string CacheKey_RawData = "RB_RawDatabaseData";

        public IndexModel(SupabaseService supabase, IMemoryCache cache)
        {
            _supabase = supabase;
            _cache = cache;
        }

        // 🚨 CHANGED: Filter is now ONLY used to pick which tab starts active client-side.
        // All snapshot lists below are always populated in full on every load.
        [BindProperty(SupportsGet = true)]
        public string Filter { get; set; } = "COH";

        [BindProperty(SupportsGet = true)]
        public bool Refresh { get; set; }

        public List<CashOnHandSnapshot> CohSnapshots { get; set; } = new();
        public List<GeneralFundMonthSnapshot> GenFundSnapshots { get; set; } = new();
        public List<WalletLedgerEntry> WalletEntries { get; set; } = new();
        public string ErrorMessage { get; set; } = "";
        public List<PledgeMonthSnapshot> PledgeSnapshots { get; set; } = new();

        // 🚨 CHANGED: split into two dedicated lists (Construction / PW) since both
        // now need to be rendered simultaneously instead of one-at-a-time per Filter.
        public List<RestrictedFundSnapshot> ConstructionSnapshots { get; set; } = new();
        public List<RestrictedFundSnapshot> PWSnapshots { get; set; } = new();

        public async Task<IActionResult> OnGetAsync()
        {
            if (!User.Identity?.IsAuthenticated ?? true) return RedirectToPage("/Login");

            try
            {
                await _supabase.InitializeAsync(true);

                // --- CACHE INVALIDATION ---
                if (Refresh)
                {
                    _cache.Remove(CacheKey_RawData);
                    return RedirectToPage(new { Filter = Filter }); // Strip 'refresh' from URL and reload
                }

                // --- CACHING IMPLEMENTATION (Matching Dashboard Pattern) ---
                if (!_cache.TryGetValue(CacheKey_RawData, out CachedLedgerData data))
                {
                    // PARALLEL FETCHING: Slash load times by fetching all 6 tables simultaneously
                    var mfbTask = _supabase.Client.From<MonthlyFundBalance>().Get();
                    var givingRecordTask = _supabase.Client.From<GivingRecord>().Get();
                    var givingEntryTask = _supabase.Client.From<GivingEntry>().Get();
                    var disbRecordTask = _supabase.Client.From<DisbursementRecord>().Get();
                    var voucherTask = _supabase.Client.From<Voucher>().Get();
                    var voucherItemTask = _supabase.Client.From<VoucherItem>().Get();

                    await Task.WhenAll(mfbTask, givingRecordTask, givingEntryTask, disbRecordTask, voucherTask, voucherItemTask);

                    data = new CachedLedgerData
                    {
                        Balances = mfbTask.Result.Models ?? new List<MonthlyFundBalance>(),
                        GivingRecords = givingRecordTask.Result.Models ?? new List<GivingRecord>(),
                        GivingEntries = givingEntryTask.Result.Models ?? new List<GivingEntry>(),
                        DisbRecords = disbRecordTask.Result.Models ?? new List<DisbursementRecord>(),
                        Vouchers = voucherTask.Result.Models ?? new List<Voucher>(),
                        VoucherItems = voucherItemTask.Result.Models ?? new List<VoucherItem>()
                    };

                    var cacheEntryOptions = new MemoryCacheEntryOptions()
                        .SetSize(1)
                        .SetAbsoluteExpiration(TimeSpan.FromMinutes(2)); // Short expiration like Dashboard

                    _cache.Set(CacheKey_RawData, data, cacheEntryOptions);
                }

                // Pre-sort balances once using LINQ
                var sortedBalances = data.Balances.OrderByDescending(b => b.ReportMonth).ToList();

                // DATA STRUCTURE OPTIMIZATION: Build O(1) Lookups
                var givingRecordDates = data.GivingRecords.ToDictionary(g => g.Id, g => g.ServiceDate.Date);
                var disbRecordDates = data.DisbRecords.ToDictionary(d => d.Id, d => d.RecordDate.Date);
                var voucherToDisbDate = data.Vouchers.ToDictionary(v => v.Id, v => disbRecordDates.ContainsKey(v.DisbursementRecordId) ? disbRecordDates[v.DisbursementRecordId] : DateTime.MinValue);

                var givingDateSet = new HashSet<DateTime>(givingRecordDates.Values);
                var disbDateSet = new HashSet<DateTime>(disbRecordDates.Values);

                var dailyStats = BuildDailyStats(data.GivingEntries, data.VoucherItems, givingRecordDates, voucherToDisbDate);

                // 🚨 CHANGED: Always compute every tab's data in one load, instead of
                // branching on Filter. This lets the Razor page render all 5 tab panes
                // at once and switch between them client-side (Bootstrap pills), exactly
                // like Admin/SystemSetup — no more server round-trip per tab click.
                GenerateCohSnapshots(sortedBalances, dailyStats, givingDateSet, disbDateSet);
                GenerateGeneralFundSnapshots(dailyStats, givingDateSet, disbDateSet);
                GeneratePledgeSnapshots(sortedBalances, dailyStats, givingDateSet, disbDateSet);
                ConstructionSnapshots = GenerateRestrictedFundSnapshots("Construction", sortedBalances, dailyStats, givingDateSet, disbDateSet);
                PWSnapshots = GenerateRestrictedFundSnapshots("Praise & Worship", sortedBalances, dailyStats, givingDateSet, disbDateSet);
            }
            catch (Exception ex)
            {
                ErrorMessage = "System Error fetching ledger data: " + ex.Message;
            }

            return Page();
        }

        private bool IsMonthComplete(int year, int month, HashSet<DateTime> givingDates, HashSet<DateTime> disbDates)
        {
            int daysInMonth = DateTime.DaysInMonth(year, month);
            for (int day = 1; day <= daysInMonth; day++)
            {
                var date = new DateTime(year, month, day);
                if (date.DayOfWeek == DayOfWeek.Sunday)
                {
                    if (!givingDates.Contains(date) || !disbDates.Contains(date))
                        return false;
                }
            }
            return true;
        }

        private Dictionary<DateTime, DailyTotals> BuildDailyStats(
            List<GivingEntry> givingEntries, List<VoucherItem> voucherItems,
            Dictionary<long, DateTime> givingDates, Dictionary<long, DateTime> disbDates)
        {
            var stats = new Dictionary<DateTime, DailyTotals>();

            DailyTotals GetStat(DateTime date)
            {
                if (!stats.ContainsKey(date)) stats[date] = new DailyTotals();
                return stats[date];
            }

            foreach (var entry in givingEntries)
            {
                if (!givingDates.TryGetValue(entry.GivingRecordId, out var date)) continue;
                var stat = GetStat(date);

                stat.IncTotal += (entry.Tithes + entry.Offerings + entry.Solomon + entry.Noah + entry.Mission + entry.Others);
                stat.IncPledges += (entry.Solomon + entry.Noah + entry.Mission);

                string fund = entry.OthersFund?.Trim() ?? "General";
                if (string.Equals(fund, "Pledges", StringComparison.OrdinalIgnoreCase)) stat.IncPledges += entry.Others;
                else if (string.Equals(fund, "Construction", StringComparison.OrdinalIgnoreCase)) stat.IncConst += entry.Others;
                else if (string.Equals(fund, "Praise & Worship", StringComparison.OrdinalIgnoreCase)) stat.IncPW += entry.Others;
            }

            foreach (var item in voucherItems)
            {
                if (!disbDates.TryGetValue(item.VoucherId, out var date)) continue;
                decimal netExp = item.Amount - item.AmountReturned;
                if (netExp <= 0) continue;

                var stat = GetStat(date);
                stat.ExpTotal += netExp;

                string fund = item.FundSource?.Trim() ?? "General";
                if (string.Equals(fund, "Pledges", StringComparison.OrdinalIgnoreCase)) stat.ExpPledges += netExp;
                else if (string.Equals(fund, "Construction", StringComparison.OrdinalIgnoreCase)) stat.ExpConst += netExp;
                else if (string.Equals(fund, "Praise & Worship", StringComparison.OrdinalIgnoreCase)) stat.ExpPW += netExp;
            }

            return stats;
        }

        private (decimal Balance, DateTime GapStart) GetStartingBalanceAndGap(List<MonthlyFundBalance> sortedBalances, string targetMonthStr, string fundName)
        {
            var lastCheckpoint = sortedBalances.FirstOrDefault(b => string.Compare(b.ReportMonth, targetMonthStr) <= 0 && b.FundName == fundName);

            if (lastCheckpoint != null)
            {
                var gapStart = DateTime.ParseExact(lastCheckpoint.ReportMonth + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture).AddMonths(1);
                return (lastCheckpoint.EndingBalance, gapStart);
            }

            return (0, new DateTime(2000, 1, 1));
        }

        private void GenerateGeneralFundSnapshots(Dictionary<DateTime, DailyTotals> stats, HashSet<DateTime> givingDateSet, HashSet<DateTime> disbDateSet)
        {
            var yearMonths = givingDateSet.Concat(disbDateSet)
                                     .GroupBy(d => new { d.Year, d.Month })
                                     .OrderBy(g => g.Key.Year)
                                     .ThenBy(g => g.Key.Month)
                                     .ToList();

            foreach (var ym in yearMonths)
            {
                if (!IsMonthComplete(ym.Key.Year, ym.Key.Month, givingDateSet, disbDateSet)) continue;

                var snapshot = new GeneralFundMonthSnapshot { Month = new DateTime(ym.Key.Year, ym.Key.Month, 1) };

                int daysInMonth = DateTime.DaysInMonth(ym.Key.Year, ym.Key.Month);
                for (int day = 1; day <= daysInMonth; day++)
                {
                    var date = new DateTime(ym.Key.Year, ym.Key.Month, day);
                    if (date.DayOfWeek == DayOfWeek.Sunday && stats.TryGetValue(date, out var dayStat))
                    {
                        decimal net = dayStat.IncTotal - dayStat.ExpTotal;
                        snapshot.Weeks.Add(new GeneralFundWeekDetail { Date = date, NetBalance = net, Pledges = dayStat.IncPledges });
                        snapshot.MonthSubtotal += (net - dayStat.IncPledges);
                    }
                }

                GenFundSnapshots.Add(snapshot);
            }
        }

        private void GenerateCohSnapshots(List<MonthlyFundBalance> sortedBalances, Dictionary<DateTime, DailyTotals> stats, HashSet<DateTime> givingDateSet, HashSet<DateTime> disbDateSet)
        {
            var yearMonths = givingDateSet.Concat(disbDateSet)
                                     .GroupBy(d => new { d.Year, d.Month })
                                     .OrderBy(g => g.Key.Year)
                                     .ThenBy(g => g.Key.Month)
                                     .ToList();

            foreach (var ym in yearMonths)
            {
                if (!IsMonthComplete(ym.Key.Year, ym.Key.Month, givingDateSet, disbDateSet)) continue;

                DateTime maxDate = new DateTime(ym.Key.Year, ym.Key.Month, DateTime.DaysInMonth(ym.Key.Year, ym.Key.Month));
                string targetMonthStr = maxDate.ToString("yyyy-MM");

                var genCheck = GetStartingBalanceAndGap(sortedBalances, targetMonthStr, "General");
                var pledgesCheck = GetStartingBalanceAndGap(sortedBalances, targetMonthStr, "Pledges");
                var constCheck = GetStartingBalanceAndGap(sortedBalances, targetMonthStr, "Construction");
                var pwCheck = GetStartingBalanceAndGap(sortedBalances, targetMonthStr, "Praise & Worship");

                decimal incGen = 0, incPledges = 0, incConst = 0, incPW = 0;
                decimal expGen = 0, expPledges = 0, expConst = 0, expPW = 0;

                foreach (var kvp in stats)
                {
                    if (kvp.Key >= genCheck.GapStart && kvp.Key <= maxDate)
                    {
                        incGen += kvp.Value.IncGen; incPledges += kvp.Value.IncPledges; incConst += kvp.Value.IncConst; incPW += kvp.Value.IncPW;
                        expGen += kvp.Value.ExpGen; expPledges += kvp.Value.ExpPledges; expConst += kvp.Value.ExpConst; expPW += kvp.Value.ExpPW;
                    }
                }

                decimal genBal = genCheck.Balance + incGen - expGen;
                decimal pledgesBal = pledgesCheck.Balance + incPledges - expPledges;
                decimal constBal = constCheck.Balance + incConst - expConst;
                decimal pwBal = pwCheck.Balance + incPW - expPW;

                decimal sisCoraTotal = genBal + pledgesBal;
                decimal pledgesActual = genBal < 0 ? (genBal + pledgesBal) : pledgesBal;

                DateTime displayDate = ym.Select(d => new DateTime(d.Year, d.Month, d.Day)).Max();

                CohSnapshots.Add(new CashOnHandSnapshot
                {
                    Date = displayDate,
                    TotalCohAmount = sisCoraTotal + constBal + pwBal,
                    CoSisCora = sisCoraTotal,
                    CoPtraEs = constBal,
                    CoPW = pwBal,
                    PledgesShouldBeTotal = pledgesBal,
                    PledgesActualTotal = pledgesActual
                });
            }
        }

        private void GenerateWalletLedger(string walletType, List<GivingRecord> givingRecords, List<GivingEntry> givingEntries, List<DisbursementRecord> disbRecords, List<Voucher> vouchers, List<VoucherItem> voucherItems)
        {
            var ledger = new List<WalletLedgerEntry>();
            decimal runningBalance = 0;

            var givingRecordDates = givingRecords.ToDictionary(g => g.Id, g => g.ServiceDate.Date);
            var disbRecordDates = disbRecords.ToDictionary(d => d.Id, d => d.RecordDate.Date);
            var voucherToDisbDate = vouchers.ToDictionary(v => v.Id, v => disbRecordDates.ContainsKey(v.DisbursementRecordId) ? disbRecordDates[v.DisbursementRecordId] : DateTime.MinValue);
            var voucherNumbers = vouchers.GroupBy(v => v.Id).ToDictionary(g => g.Key, g => g.First().VoucherNumber);

            var inflows = new List<WalletLedgerEntry>();
            foreach (var entry in givingEntries)
            {
                if (!givingRecordDates.TryGetValue(entry.GivingRecordId, out DateTime date)) continue;

                decimal amount = 0;
                string fund = entry.OthersFund?.Trim() ?? "General";

                if (walletType == "General")
                {
                    amount = entry.Tithes + entry.Offerings;
                    if (string.Equals(fund, "General", StringComparison.OrdinalIgnoreCase)) amount += entry.Others;
                }
                else if (walletType == "Pledges")
                {
                    amount = entry.Solomon + entry.Noah + entry.Mission;
                    if (string.Equals(fund, "Pledges", StringComparison.OrdinalIgnoreCase)) amount += entry.Others;
                }
                else if (string.Equals(fund, walletType, StringComparison.OrdinalIgnoreCase))
                {
                    amount = entry.Others;
                }

                if (amount > 0)
                {
                    inflows.Add(new WalletLedgerEntry { Date = date, Description = "Inflow - Giving Record", Amount = amount });
                }
            }

            var outflows = new List<WalletLedgerEntry>();
            foreach (var item in voucherItems)
            {
                string fund = item.FundSource?.Trim() ?? "General";
                if (!string.Equals(fund, walletType, StringComparison.OrdinalIgnoreCase)) continue;

                if (!voucherToDisbDate.TryGetValue(item.VoucherId, out DateTime date)) continue;
                string vNum = voucherNumbers.ContainsKey(item.VoucherId) ? voucherNumbers[item.VoucherId] : "Unknown";

                decimal netOutflow = item.Amount - item.AmountReturned;
                if (netOutflow > 0)
                {
                    outflows.Add(new WalletLedgerEntry { Date = date, Description = $"Voucher {vNum} - {item.Particular}", Amount = -netOutflow });
                }
            }

            var allTransactions = inflows.Concat(outflows).OrderBy(t => t.Date).ToList();

            foreach (var trans in allTransactions)
            {
                runningBalance += trans.Amount;
                ledger.Add(new WalletLedgerEntry
                {
                    Date = trans.Date,
                    Description = trans.Description,
                    Amount = trans.Amount,
                    RunningBalance = runningBalance
                });
            }

            WalletEntries = ledger.OrderByDescending(l => l.Date).ToList();
        }

        private void GeneratePledgeSnapshots(List<MonthlyFundBalance> sortedBalances, Dictionary<DateTime, DailyTotals> stats, HashSet<DateTime> givingDateSet, HashSet<DateTime> disbDateSet)
        {
            var yearMonths = givingDateSet.Concat(disbDateSet)
                                     .GroupBy(d => new { d.Year, d.Month })
                                     .OrderBy(g => g.Key.Year)
                                     .ThenBy(g => g.Key.Month)
                                     .ToList();

            decimal currentRunningBalance = 0;
            bool isFirst = true;

            foreach (var ym in yearMonths)
            {
                if (!IsMonthComplete(ym.Key.Year, ym.Key.Month, givingDateSet, disbDateSet)) continue;

                DateTime currentMonthDate = new DateTime(ym.Key.Year, ym.Key.Month, 1);

                if (isFirst)
                {
                    string targetMonthStr = currentMonthDate.AddMonths(-1).ToString("yyyy-MM");
                    var check = GetStartingBalanceAndGap(sortedBalances, targetMonthStr, "Pledges");
                    currentRunningBalance = check.Balance;

                    foreach (var kvp in stats)
                    {
                        if (kvp.Key >= check.GapStart && kvp.Key < currentMonthDate)
                        {
                            currentRunningBalance += (kvp.Value.IncPledges - kvp.Value.ExpPledges);
                        }
                    }
                    isFirst = false;
                }

                decimal monthPledges = 0;
                int daysInMonth = DateTime.DaysInMonth(ym.Key.Year, ym.Key.Month);
                for (int day = 1; day <= daysInMonth; day++)
                {
                    var date = new DateTime(ym.Key.Year, ym.Key.Month, day);
                    if (stats.TryGetValue(date, out var dayStat))
                    {
                        monthPledges += (dayStat.IncPledges - dayStat.ExpPledges);
                    }
                }

                var snapshot = new PledgeMonthSnapshot
                {
                    Month = currentMonthDate,
                    BeginningBalance = currentRunningBalance,
                    CurrentMonthPledges = monthPledges
                };

                PledgeSnapshots.Add(snapshot);
                currentRunningBalance = snapshot.EndingBalance;
            }
        }

        // 🚨 CHANGED: now returns a fresh List<RestrictedFundSnapshot> instead of writing
        // to a single shared field, so Construction and Praise & Worship can both be
        // computed and displayed at the same time.
        private List<RestrictedFundSnapshot> GenerateRestrictedFundSnapshots(string fundName, List<MonthlyFundBalance> sortedBalances, Dictionary<DateTime, DailyTotals> stats, HashSet<DateTime> givingDateSet, HashSet<DateTime> disbDateSet)
        {
            var result = new List<RestrictedFundSnapshot>();

            var yearMonths = givingDateSet.Concat(disbDateSet)
                                     .GroupBy(d => new { d.Year, d.Month })
                                     .OrderBy(g => g.Key.Year)
                                     .ThenBy(g => g.Key.Month)
                                     .ToList();

            decimal currentRunningBalance = 0;
            bool isFirst = true;

            foreach (var ym in yearMonths)
            {
                if (!IsMonthComplete(ym.Key.Year, ym.Key.Month, givingDateSet, disbDateSet)) continue;

                DateTime currentMonthDate = new DateTime(ym.Key.Year, ym.Key.Month, 1);

                if (isFirst)
                {
                    string targetMonthStr = currentMonthDate.AddMonths(-1).ToString("yyyy-MM");
                    var check = GetStartingBalanceAndGap(sortedBalances, targetMonthStr, fundName);
                    currentRunningBalance = check.Balance;

                    foreach (var kvp in stats)
                    {
                        if (kvp.Key >= check.GapStart && kvp.Key < currentMonthDate)
                        {
                            decimal inc = fundName == "Construction" ? kvp.Value.IncConst : kvp.Value.IncPW;
                            decimal exp = fundName == "Construction" ? kvp.Value.ExpConst : kvp.Value.ExpPW;
                            currentRunningBalance += (inc - exp);
                        }
                    }
                    isFirst = false;
                }

                decimal monthInflows = 0;
                decimal monthOutflows = 0;
                int daysInMonth = DateTime.DaysInMonth(ym.Key.Year, ym.Key.Month);

                for (int day = 1; day <= daysInMonth; day++)
                {
                    var date = new DateTime(ym.Key.Year, ym.Key.Month, day);
                    if (stats.TryGetValue(date, out var dayStat))
                    {
                        monthInflows += fundName == "Construction" ? dayStat.IncConst : dayStat.IncPW;
                        monthOutflows += fundName == "Construction" ? dayStat.ExpConst : dayStat.ExpPW;
                    }
                }

                var snapshot = new RestrictedFundSnapshot
                {
                    Month = currentMonthDate,
                    BeginningBalance = currentRunningBalance,
                    Inflows = monthInflows,
                    Outflows = monthOutflows
                };

                result.Add(snapshot);
                currentRunningBalance = snapshot.EndingBalance;
            }

            return result;
        }
    }

    // --- CACHE HELPER CLASS ---
    public class CachedLedgerData
    {
        public List<MonthlyFundBalance> Balances { get; set; }
        public List<GivingRecord> GivingRecords { get; set; }
        public List<GivingEntry> GivingEntries { get; set; }
        public List<DisbursementRecord> DisbRecords { get; set; }
        public List<Voucher> Vouchers { get; set; }
        public List<VoucherItem> VoucherItems { get; set; }
    }

    // [Model classes remain unchanged below]
    public class DailyTotals
    {
        public decimal IncTotal { get; set; }
        public decimal IncPledges { get; set; }
        public decimal IncConst { get; set; }
        public decimal IncPW { get; set; }
        public decimal IncGen => IncTotal - IncPledges - IncConst - IncPW;

        public decimal ExpTotal { get; set; }
        public decimal ExpPledges { get; set; }
        public decimal ExpConst { get; set; }
        public decimal ExpPW { get; set; }
        public decimal ExpGen => ExpTotal - ExpPledges - ExpConst - ExpPW;
    }

    public class GeneralFundWeekDetail
    {
        public DateTime Date { get; set; }
        public decimal NetBalance { get; set; }
        public decimal Pledges { get; set; }
    }

    public class GeneralFundMonthSnapshot
    {
        public DateTime Month { get; set; }
        public List<GeneralFundWeekDetail> Weeks { get; set; } = new();
        public decimal MonthSubtotal { get; set; }
    }

    public class PledgeMonthSnapshot
    {
        public DateTime Month { get; set; }
        public decimal BeginningBalance { get; set; }
        public decimal CurrentMonthPledges { get; set; }
        public decimal EndingBalance => BeginningBalance + CurrentMonthPledges;
    }

    public class RestrictedFundSnapshot
    {
        public DateTime Month { get; set; }
        public decimal BeginningBalance { get; set; }
        public decimal Inflows { get; set; }
        public decimal Outflows { get; set; }
        public decimal EndingBalance => BeginningBalance + Inflows - Outflows;
    }
}