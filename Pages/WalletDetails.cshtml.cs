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
    public class WalletDetailsModel : PageModel
    {
        private readonly SupabaseService _supabase;
        private readonly FinancialCalculatorService _calculator;
        private readonly IMemoryCache _cache;

        public WalletDetailsModel(SupabaseService supabase, FinancialCalculatorService calculator, IMemoryCache cache)
        {
            _supabase = supabase;
            _calculator = calculator;
            _cache = cache;
        }

        public string FundName { get; set; } = "";
        public string MonthLabel { get; set; } = "";
        public string SelectedMonth { get; set; } = "";
        public List<string> AvailableMonths { get; set; } = new();

        [BindProperty(SupportsGet = true)]
        public string SortBy { get; set; } = "Date";

        public List<IncomeDetailVm> Incomes { get; set; } = new();
        public List<ExpenseGroupVm> ExpenseGroups { get; set; } = new();

        public decimal TotalIncome => Incomes.Sum(x => x.TotalAmount);
        public decimal TotalExpense => ExpenseGroups.Sum(g => g.GroupTotal);
        public decimal NetFlow => TotalIncome - TotalExpense;

        public decimal BookBalance { get; set; } = 0;
        public decimal CashOnHand { get; set; } = 0;

        // All-Time Pledges Breakdown Properties
        public decimal AbsoluteLiveEndBalance { get; set; } = 0;
        public decimal CarriedForwardBalance { get; set; } = 0;
        public decimal LifetimeSolomon { get; set; } = 0;
        public decimal LifetimeNoah { get; set; } = 0;
        public decimal LifetimeMission { get; set; } = 0;
        public decimal LifetimeOtherPledges { get; set; } = 0;
        public decimal LifetimePledgesExpense { get; set; } = 0;
        public decimal NetPledgesBalance => (LifetimeSolomon + LifetimeNoah + LifetimeMission + LifetimeOtherPledges) - LifetimePledgesExpense;

        // Contextual Smart Analytics
        public string InsightTitle { get; set; } = "Smart Insight";
        public string InsightLabel { get; set; } = "N/A";
        public string InsightAmount { get; set; } = "₱ 0.00";

        public List<PledgeHistoryRowVm> PledgeHistory { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(string month, string fund)
        {
            if (string.IsNullOrWhiteSpace(fund))
            {
                var savedFund = HttpContext.Session.GetString("Wallet_Fund");
                var savedMonth = HttpContext.Session.GetString("Wallet_Month");
                var savedSort = HttpContext.Session.GetString("Wallet_SortBy");

                if (!string.IsNullOrEmpty(savedFund))
                {
                    return RedirectToPage(new { fund = savedFund, month = savedMonth, SortBy = savedSort ?? "Date" });
                }
                return RedirectToPage("/Index");
            }

            // Save current view to session
            HttpContext.Session.SetString("Wallet_Fund", fund);
            HttpContext.Session.SetString("Wallet_Month", month ?? "");
            HttpContext.Session.SetString("Wallet_SortBy", SortBy ?? "Date");

            await _supabase.InitializeAsync(true);
            FundName = fund;

            // 🚨 2. OPTIMIZATION: CACHE AVAILABLE MONTHS 🚨
            if (!_cache.TryGetValue("AvailableWalletMonths", out List<string> cachedMonths))
            {
                var givingDatesTask = _supabase.Client.From<GivingRecord>().Select("service_date").Get();
                var disbDatesTask = _supabase.Client.From<DisbursementRecord>().Select("record_date").Get();
                await Task.WhenAll(givingDatesTask, disbDatesTask);

                var allDates = new List<DateTime>();
                if (givingDatesTask.Result.Models != null) allDates.AddRange(givingDatesTask.Result.Models.Select(m => m.ServiceDate));
                if (disbDatesTask.Result.Models != null) allDates.AddRange(disbDatesTask.Result.Models.Select(m => m.RecordDate));

                cachedMonths = allDates.Select(d => d.ToString("yyyy-MM")).Distinct().OrderByDescending(m => m).ToList();
                var monthsOptions = new MemoryCacheEntryOptions().SetSize(1).SetAbsoluteExpiration(TimeSpan.FromHours(1));
                _cache.Set("AvailableWalletMonths", cachedMonths, monthsOptions);
            }
            AvailableMonths = cachedMonths ?? new List<string>();

            if (string.IsNullOrWhiteSpace(month))
                month = AvailableMonths.FirstOrDefault() ?? DateTime.Today.ToString("yyyy-MM");

            SelectedMonth = month;
            DateTime firstDay = DateTime.ParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
            DateTime lastDay = firstDay.AddMonths(1).AddDays(-1);
            MonthLabel = firstDay.ToString("MMMM yyyy");

            string firstDayStr = firstDay.ToString("yyyy-MM-dd");
            string lastDayStr = lastDay.ToString("yyyy-MM-dd");

            // 🚨 3. RAM CACHE FOR THE ENTIRE MONTH'S RAW DATA 🚨
            string monthDataKey = $"WalletMonthData_{SelectedMonth}";

            if (!_cache.TryGetValue(monthDataKey, out WalletMonthRawCache rawData) || rawData == null)
            {
                var givingTask = _supabase.Client.From<GivingRecord>()
                    .Filter("service_date", Operator.GreaterThanOrEqual, firstDayStr)
                    .Filter("service_date", Operator.LessThanOrEqual, lastDayStr).Get();
                var disbTask = _supabase.Client.From<DisbursementRecord>()
                    .Filter("record_date", Operator.GreaterThanOrEqual, firstDayStr)
                    .Filter("record_date", Operator.LessThanOrEqual, lastDayStr).Get();

                await Task.WhenAll(givingTask, disbTask);

                var givingRecords = givingTask.Result.Models?.OrderBy(x => x.ServiceDate).ToList() ?? new List<GivingRecord>();
                var disbRecords = disbTask.Result.Models ?? new List<DisbursementRecord>();

                var givingRecordIds = givingRecords.Select(r => (object)r.Id).ToList();
                var disbRecordIds = disbRecords.Select(d => (object)d.Id).ToList();

                var entriesTask = givingRecordIds.Any()
            ? _supabase.Client.From<GivingEntry>().Filter("giving_record_id", Operator.In, givingRecordIds).Get()
            : Task.FromResult((Supabase.Postgrest.Responses.ModeledResponse<GivingEntry>?)null);

                var vouchersTask = disbRecordIds.Any()
                    ? _supabase.Client.From<Voucher>().Filter("disbursement_record_id", Operator.In, disbRecordIds).Get()
                    : Task.FromResult((Supabase.Postgrest.Responses.ModeledResponse<Voucher>?)null);

                await Task.WhenAll(entriesTask, vouchersTask);

                var allVouchers = vouchersTask.Result?.Models ?? new List<Voucher>();
                var voucherIds = allVouchers.Select(v => (object)v.Id).ToList();

                var itemsTask = voucherIds.Any()
                    ? _supabase.Client.From<VoucherItem>().Filter("voucher_id", Operator.In, voucherIds).Get()
                    : Task.FromResult((Supabase.Postgrest.Responses.ModeledResponse<VoucherItem>?)null);

                await itemsTask;

                rawData = new WalletMonthRawCache
                {
                    GivingRecords = givingRecords,
                    DisbRecords = disbRecords,
                    GivingEntries = entriesTask.Result?.Models ?? new List<GivingEntry>(),
                    Vouchers = allVouchers,
                    VoucherItems = itemsTask.Result?.Models ?? new List<VoucherItem>()
                };

                var rawDataOptions = new MemoryCacheEntryOptions()
                    .SetSize(10) // 🚨 Heavy Object: Costs 10 points
                    .SetAbsoluteExpiration(TimeSpan.FromHours(1));
                _cache.Set(monthDataKey, rawData, rawDataOptions);
            }

            var givingRecordsLocal = rawData.GivingRecords;
            var disbRecordsLocal = rawData.DisbRecords;
            var allGivingEntries = rawData.GivingEntries;
            var allVouchersLocal = rawData.Vouchers;
            var allVoucherItems = rawData.VoucherItems;

            // ==========================================
            // 4. PROCESS INCOME IN MEMORY (NO N+1)
            // ==========================================
            foreach (var record in givingRecordsLocal)
            {
                var entries = allGivingEntries.Where(e => e.GivingRecordId == record.Id).ToList();

                decimal tithes = 0, offerings = 0, pledges = 0, others = 0;
                decimal solomon = 0, noah = 0, mission = 0, otherPledges = 0;

                if (fund.Equals("General", StringComparison.OrdinalIgnoreCase))
                {
                    tithes = entries.Sum(e => e.Tithes);
                    offerings = entries.Sum(e => e.Offerings);
                    others = entries.Where(e => string.IsNullOrWhiteSpace(e.OthersFund) || e.OthersFund.Trim().Equals("General", StringComparison.OrdinalIgnoreCase)).Sum(e => e.Others);
                }
                else if (fund.Equals("Pledges", StringComparison.OrdinalIgnoreCase))
                {
                    solomon = entries.Sum(e => e.Solomon);
                    noah = entries.Sum(e => e.Noah);
                    mission = entries.Sum(e => e.Mission);
                    otherPledges = entries.Where(e => !string.IsNullOrWhiteSpace(e.OthersFund) && e.OthersFund.Trim().Equals("Pledges", StringComparison.OrdinalIgnoreCase)).Sum(e => e.Others);
                    pledges = solomon + noah + mission + otherPledges;
                }
                else
                {
                    others = entries.Where(e => !string.IsNullOrWhiteSpace(e.OthersFund) && e.OthersFund.Trim().Equals(fund, StringComparison.OrdinalIgnoreCase)).Sum(e => e.Others);
                }

                decimal total = tithes + offerings + pledges + others;

                if (total > 0)
                {
                    Incomes.Add(new IncomeDetailVm
                    {
                        RecordId = record.Id.ToString(),
                        Date = record.ServiceDate,
                        RecordCode = record.RecordCode,
                        Tithes = tithes,
                        Offerings = offerings,
                        Pledges = pledges,
                        Others = others,
                        Solomon = solomon,
                        Noah = noah,
                        Mission = mission,
                        OtherPledges = otherPledges,
                        TotalAmount = total
                    });
                }
            }

            // ==========================================
            // 5. PROCESS EXPENSES IN MEMORY (NO N+1)
            // ==========================================
            var allExpenses = new List<ExpenseDetailVm>();

            foreach (var dr in disbRecordsLocal)
            {
                var vouchers = allVouchersLocal.Where(v => v.DisbursementRecordId == dr.Id).ToList();
                foreach (var v in vouchers)
                {
                    var items = allVoucherItems.Where(i => i.VoucherId == v.Id).ToList();
                    foreach (var item in items)
                    {
                        decimal netExp = item.Amount - item.AmountReturned;
                        if (netExp <= 0) continue;

                        string fundSource = string.IsNullOrWhiteSpace(item.FundSource) ? "General" : item.FundSource.Trim();

                        if (fundSource.Equals(fund, StringComparison.OrdinalIgnoreCase))
                        {
                            allExpenses.Add(new ExpenseDetailVm
                            {
                                Date = dr.RecordDate,
                                Ministry = v.Ministry,
                                Payee = v.Payee,
                                Particulars = item.Particular,
                                Amount = netExp
                            });
                        }
                    }
                }
            }

            if (SortBy == "Ministry")
            {
                ExpenseGroups = allExpenses.GroupBy(e => e.Ministry).OrderBy(g => g.Key)
                    .Select(g => new ExpenseGroupVm { GroupName = g.Key, Expenses = g.ToList() }).ToList();
            }
            else if (SortBy == "Highest")
            {
                ExpenseGroups = new List<ExpenseGroupVm> {
            new ExpenseGroupVm { GroupName = "All Transactions (Highest to Lowest)", Expenses = allExpenses.OrderByDescending(e => e.Amount).ToList() }
        };
            }
            else if (SortBy == "Lowest")
            {
                ExpenseGroups = new List<ExpenseGroupVm> {
            new ExpenseGroupVm { GroupName = "All Transactions (Lowest to Highest)", Expenses = allExpenses.OrderBy(e => e.Amount).ToList() }
        };
            }
            else
            {
                ExpenseGroups = allExpenses.GroupBy(e => e.Date.Date).OrderBy(g => g.Key)
                    .Select(g => new ExpenseGroupVm { GroupName = g.Key.ToString("MMMM dd, yyyy"), Expenses = g.ToList() }).ToList();
            }

            // ==========================================
            // 6. GENERATE CONTEXTUAL SMART INSIGHTS
            // ==========================================
            if (fund.Equals("General", StringComparison.OrdinalIgnoreCase) && allExpenses.Any())
            {
                var top = allExpenses.GroupBy(e => e.Ministry).OrderByDescending(g => g.Sum(x => x.Amount)).FirstOrDefault();
                InsightTitle = "Expense Analysis";
                InsightLabel = $"Highest Ministry: {top?.Key}";
                InsightAmount = $"₱ {top?.Sum(x => x.Amount).ToString("N2")}";
            }

            decimal liveInc = TotalIncome;
            decimal liveExp = TotalExpense;
            BookBalance = await GetAbsoluteLiveEndBalanceAsync(fund, liveInc, liveExp);

            // ==========================================
            // 7. CALCULATE ALL-TIME PLEDGES DISTRIBUTION
            // ==========================================

            if (fund.Equals("Pledges", StringComparison.OrdinalIgnoreCase))
            {
                AbsoluteLiveEndBalance = BookBalance;
                await CalculateLifetimePledgesAsync(lastDay, SelectedMonth);
                CarriedForwardBalance = AbsoluteLiveEndBalance - NetPledgesBalance;

                await LoadPledgeHistoryAsync(SelectedMonth);
            }

            CashOnHand = BookBalance;

            return Page();
        }

        private async Task CalculateLifetimePledgesAsync(DateTime endOfMonth, string selectedMonthStr)
        {
            if (!_cache.TryGetValue("AllMonthlyPledgeBreakdowns", out List<MonthlyPledgeBreakdown> allBreakdowns))
            {
                var breakdownResp = await _supabase.Client.From<MonthlyPledgeBreakdown>().Get();
                allBreakdowns = breakdownResp.Models ?? new List<MonthlyPledgeBreakdown>();
                var breakdownOptions = new MemoryCacheEntryOptions().SetSize(2).SetAbsoluteExpiration(TimeSpan.FromHours(1));
                _cache.Set("AllMonthlyPledgeBreakdowns", allBreakdowns, breakdownOptions);
            }

            var lastCheckpoint = allBreakdowns
                .Where(b => string.Compare(b.ReportMonth, selectedMonthStr) <= 0)
                .OrderByDescending(b => b.ReportMonth)
                .FirstOrDefault();

            DateTime gapStart = lastCheckpoint != null
                ? DateTime.ParseExact(lastCheckpoint.ReportMonth + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture).AddMonths(1)
                : new DateTime(2000, 1, 1);

            if (lastCheckpoint != null)
            {
                LifetimeSolomon = lastCheckpoint.SolomonBalance;
                LifetimeNoah = lastCheckpoint.NoahBalance;
                LifetimeMission = lastCheckpoint.MissionBalance;
                LifetimeOtherPledges = lastCheckpoint.OthersBalance;
            }

            if (gapStart <= endOfMonth)
            {
                string gapKey = $"PledgeIncGap_{gapStart:yyyyMMdd}_{endOfMonth:yyyyMMdd}";
                if (!_cache.TryGetValue(gapKey, out PledgeGapResult gapResult))
                {
                    var givingResp = await _supabase.Client.From<GivingRecord>()
                        .Filter("service_date", Operator.GreaterThanOrEqual, gapStart.ToString("yyyy-MM-dd"))
                        .Filter("service_date", Operator.LessThanOrEqual, endOfMonth.ToString("yyyy-MM-dd")).Get();

                    var recordIds = givingResp.Models?.Select(r => (object)r.Id).ToList() ?? new List<object>();
                    var allEntries = new List<GivingEntry>();

                    if (recordIds.Any())
                    {
                        var entryResp = await _supabase.Client.From<GivingEntry>().Filter("giving_record_id", Operator.In, recordIds).Get();
                        allEntries = entryResp.Models ?? new List<GivingEntry>();
                    }

                    gapResult = new PledgeGapResult
                    {
                        Solomon = allEntries.Sum(e => e.Solomon),
                        Noah = allEntries.Sum(e => e.Noah),
                        Mission = allEntries.Sum(e => e.Mission),
                        OtherPledges = allEntries.Where(e => !string.IsNullOrWhiteSpace(e.OthersFund) && e.OthersFund.Trim().Equals("Pledges", StringComparison.OrdinalIgnoreCase)).Sum(e => e.Others)
                    };

                    var gapOptions = new MemoryCacheEntryOptions().SetSize(1).SetAbsoluteExpiration(TimeSpan.FromHours(12));
                    _cache.Set(gapKey, gapResult, gapOptions);
                }

                LifetimeSolomon += gapResult.Solomon;
                LifetimeNoah += gapResult.Noah;
                LifetimeMission += gapResult.Mission;
                LifetimeOtherPledges += gapResult.OtherPledges;
            }

            string expKey = $"PledgeExp_{endOfMonth:yyyyMMdd}";
            if (!_cache.TryGetValue(expKey, out decimal cachedExp))
            {
                var disbResp = await _supabase.Client.From<DisbursementRecord>()
                    .Filter("record_date", Operator.LessThanOrEqual, endOfMonth.ToString("yyyy-MM-dd")).Get();
                var disbRecordIds = disbResp.Models?.Select(r => (object)r.Id).ToList() ?? new List<object>();

                var allItems = new List<VoucherItem>();
                if (disbRecordIds.Any())
                {
                    var vResp = await _supabase.Client.From<Voucher>().Filter("disbursement_record_id", Operator.In, disbRecordIds).Get();
                    var voucherIds = vResp.Models?.Select(v => (object)v.Id).ToList() ?? new List<object>();

                    if (voucherIds.Any())
                    {
                        var iResp = await _supabase.Client.From<VoucherItem>().Filter("voucher_id", Operator.In, voucherIds).Get();
                        allItems = iResp.Models ?? new List<VoucherItem>();
                    }
                }

                cachedExp = allItems
                    .Where(i => !string.IsNullOrWhiteSpace(i.FundSource) && i.FundSource.Trim().Equals("Pledges", StringComparison.OrdinalIgnoreCase))
                    .Sum(i => i.Amount - i.AmountReturned);

                var expOptions = new MemoryCacheEntryOptions().SetSize(1).SetAbsoluteExpiration(TimeSpan.FromHours(12));
                _cache.Set(expKey, cachedExp, expOptions);
            }

            LifetimePledgesExpense = cachedExp;
        }

        private async Task<decimal> GetAbsoluteLiveEndBalanceAsync(string fund, decimal liveInc, decimal liveExp)
        {
            if (!_cache.TryGetValue("AllMonthlyFundBalances", out List<MonthlyFundBalance> allBalances))
            {
                var mfbResp = await _supabase.Client.From<MonthlyFundBalance>().Get();
                allBalances = mfbResp.Models ?? new List<MonthlyFundBalance>();
                var balOptions = new MemoryCacheEntryOptions().SetSize(2).SetAbsoluteExpiration(TimeSpan.FromHours(1));
                _cache.Set("AllMonthlyFundBalances", allBalances, balOptions);
            }

            var lastCheckpoint = allBalances.Where(b => string.Compare(b.ReportMonth, SelectedMonth) < 0).OrderByDescending(b => b.ReportMonth).FirstOrDefault();

            decimal begBal = 0;
            DateTime gapStart = DateTime.MinValue;

            if (lastCheckpoint != null)
            {
                begBal = allBalances.FirstOrDefault(b => b.ReportMonth == lastCheckpoint.ReportMonth && b.FundName.Equals(fund, StringComparison.OrdinalIgnoreCase))?.EndingBalance ?? 0;
                DateTime cpDate = DateTime.ParseExact(lastCheckpoint.ReportMonth + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
                gapStart = cpDate.AddMonths(1);
            }
            else
            {
                gapStart = new DateTime(2000, 1, 1);
            }

            DateTime targetFirstDay = DateTime.ParseExact(SelectedMonth + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
            DateTime gapEnd = targetFirstDay.AddDays(-1);

            if (gapStart <= gapEnd)
            {
                string gapCacheKey = $"WalletGap_{gapStart:yyyyMMdd}_{gapEnd:yyyyMMdd}_{fund}";
                if (!_cache.TryGetValue(gapCacheKey, out decimal gapNet))
                {
                    var inc = await _calculator.CalculateIncomeForFundAsync(gapStart, gapEnd, fund);
                    var exp = await _calculator.CalculateExpenseForFundAsync(gapStart, gapEnd, fund);
                    gapNet = inc - exp;
                    var walletGapOptions = new MemoryCacheEntryOptions().SetSize(1).SetAbsoluteExpiration(TimeSpan.FromHours(12));
                    _cache.Set(gapCacheKey, gapNet, walletGapOptions);
                }
                begBal += gapNet;
            }

            return begBal + liveInc - liveExp;
        }

        private async Task LoadPledgeHistoryAsync(string selectedMonthStr)
        {
            string historyKey = $"PledgeHistory_{selectedMonthStr}";
            if (_cache.TryGetValue(historyKey, out List<PledgeHistoryRowVm> cachedHistory))
            {
                PledgeHistory = cachedHistory;
                return;
            }

            DateTime targetMonth = DateTime.ParseExact(selectedMonthStr + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
            int targetYear = targetMonth.Year;

            string prevYearDec = $"{targetYear - 1}-12";
            var prevBreakdownResp = await _supabase.Client.From<MonthlyPledgeBreakdown>()
                .Filter("report_month", Operator.Equals, prevYearDec).Get();
            var prevBreakdown = prevBreakdownResp.Models.FirstOrDefault();

            decimal runningSolomon = prevBreakdown?.SolomonBalance ?? 0;
            decimal runningNoah = prevBreakdown?.NoahBalance ?? 0;
            decimal runningMission = prevBreakdown?.MissionBalance ?? 0;

            PledgeHistory = new List<PledgeHistoryRowVm>();
            PledgeHistory.Add(new PledgeHistoryRowVm
            {
                MonthLabel = "Beg",
                DailySolomon = runningSolomon,
                DailyNoah = runningNoah,
                DailyMission = runningMission,
                DailyTotal = runningSolomon + runningNoah + runningMission
            });

            string yearStartStr = $"{targetYear}-01-01";
            string targetEndStr = targetMonth.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd");

            var givingResp = await _supabase.Client.From<GivingRecord>()
                .Filter("service_date", Operator.GreaterThanOrEqual, yearStartStr)
                .Filter("service_date", Operator.LessThanOrEqual, targetEndStr)
                .Get();

            var givingRecords = givingResp.Models?.OrderBy(r => r.ServiceDate).ToList() ?? new List<GivingRecord>();
            var recordIds = givingRecords.Select(r => (object)r.Id).ToList();

            var allEntries = new List<GivingEntry>();
            if (recordIds.Any())
            {
                var entryResp = await _supabase.Client.From<GivingEntry>()
                    .Filter("giving_record_id", Operator.In, recordIds).Get();
                allEntries = entryResp.Models ?? new List<GivingEntry>();
            }

            var recordsByMonth = givingRecords.GroupBy(r => r.ServiceDate.Month).OrderBy(g => g.Key).ToList();

            for (int m = 1; m <= targetMonth.Month; m++)
            {
                var monthRecords = recordsByMonth.FirstOrDefault(g => g.Key == m);
                if (monthRecords != null)
                {
                    foreach (var record in monthRecords)
                    {
                        var entries = allEntries.Where(e => e.GivingRecordId == record.Id).ToList();
                        decimal dailySol = entries.Sum(e => e.Solomon);
                        decimal dailyNoah = entries.Sum(e => e.Noah);
                        decimal dailyMiss = entries.Sum(e => e.Mission);
                        decimal dailyTot = dailySol + dailyNoah + dailyMiss;

                        if (dailyTot > 0)
                        {
                            PledgeHistory.Add(new PledgeHistoryRowVm
                            {
                                Date = record.ServiceDate,
                                RecordId = record.Id.ToString(),
                                RecordCode = record.RecordCode,
                                DailySolomon = dailySol,
                                DailyNoah = dailyNoah,
                                DailyMission = dailyMiss,
                                DailyTotal = dailyTot
                            });

                            runningSolomon += dailySol;
                            runningNoah += dailyNoah;
                            runningMission += dailyMiss;
                        }
                    }
                }

                PledgeHistory.Add(new PledgeHistoryRowVm
                {
                    IsMonthlySummary = true,
                    CumulativeSolomon = runningSolomon,
                    CumulativeNoah = runningNoah,
                    CumulativeMission = runningMission,
                    CumulativeTotal = runningSolomon + runningNoah + runningMission,
                    MonthLabel = new DateTime(targetYear, m, 1).ToString("MMMM")
                });
            }

            var historyOptions = new MemoryCacheEntryOptions()
                .SetSize(5)
                .SetAbsoluteExpiration(TimeSpan.FromHours(1));
            _cache.Set(historyKey, PledgeHistory, historyOptions);
        }
    }

    public class IncomeDetailVm
    {
        public string RecordId { get; set; } = "";
        public DateTime Date { get; set; }
        public string RecordCode { get; set; } = "";
        public decimal Tithes { get; set; }
        public decimal Offerings { get; set; }
        public decimal Pledges { get; set; }
        public decimal Others { get; set; }
        public decimal Solomon { get; set; }
        public decimal Noah { get; set; }
        public decimal Mission { get; set; }
        public decimal OtherPledges { get; set; }
        public decimal TotalAmount { get; set; }
    }

    public class ExpenseGroupVm
    {
        public string GroupName { get; set; } = "";
        public List<ExpenseDetailVm> Expenses { get; set; } = new();
        public decimal GroupTotal => Expenses.Sum(x => x.Amount);
    }

    public class ExpenseDetailVm
    {
        public DateTime Date { get; set; }
        public string Ministry { get; set; } = "";
        public string Payee { get; set; } = "";
        public string Particulars { get; set; } = "";
        public decimal Amount { get; set; }
    }

    public class PledgeHistoryRowVm
    {
        public bool IsMonthlySummary { get; set; }
        public DateTime? Date { get; set; }
        public string RecordId { get; set; } = "";
        public string RecordCode { get; set; } = "";
        public string MonthLabel { get; set; } = "";

        public decimal? DailySolomon { get; set; }
        public decimal? DailyNoah { get; set; }
        public decimal? DailyMission { get; set; }
        public decimal? DailyTotal { get; set; }

        public decimal? CumulativeSolomon { get; set; }
        public decimal? CumulativeNoah { get; set; }
        public decimal? CumulativeMission { get; set; }
        public decimal? CumulativeTotal { get; set; }
    }
    public class PledgeGapResult
    {
        public decimal Solomon { get; set; }
        public decimal Noah { get; set; }
        public decimal Mission { get; set; }
        public decimal OtherPledges { get; set; }
    }

    public class WalletMonthRawCache
    {
        public List<GivingRecord> GivingRecords { get; set; } = new();
        public List<DisbursementRecord> DisbRecords { get; set; } = new();
        public List<GivingEntry> GivingEntries { get; set; } = new();
        public List<Voucher> Vouchers { get; set; } = new();
        public List<VoucherItem> VoucherItems { get; set; } = new();
    }
}