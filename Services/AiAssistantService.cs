using System;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using System.Net.Http;
using System.Linq;
using System.Globalization;
using acc_finance.Models;
using static Supabase.Postgrest.Constants;

namespace acc_finance.Services
{

    public class AiAssistantService
    {
        private readonly SupabaseService _supabase;
        private readonly HttpClient _httpClient;
        private readonly string _groqApiKey;
        private readonly IMemoryCache _cache;

        public AiAssistantService(SupabaseService supabase, HttpClient httpClient, IConfiguration config, IMemoryCache cache)
        {
            _supabase = supabase;
            _httpClient = httpClient;
            _groqApiKey = config["Groq:ApiKey"] ?? "";
            _cache = cache;
        }

        private string GetChatHistory() => _cache.TryGetValue("AiChatHistory", out string history) ? history : "";

        private void AppendToHistory(string role, string message)
        {
            var history = GetChatHistory() + $"\n{role}: {message}";
            if (history.Length > 2500) history = history.Substring(history.Length - 2500);

            var cacheOptions = new MemoryCacheEntryOptions()
                .SetSize(1) // 1 Point for a basic text string
                .SetAbsoluteExpiration(TimeSpan.FromHours(1));

            _cache.Set("AiChatHistory", history, cacheOptions);
        }

        public async Task<string> ProcessUserQuestionAsync(string userQuestion)
        {
            string chatHistory = GetChatHistory();

            // 1. Ask the AI to act as a "Search Translator"
            FinancialFilter filter = await ParseUserIntentAsync(userQuestion, chatHistory);

            if (filter.IsSensitiveQuery)
            {
                return @"<div class='text-danger fw-bold'><i class='bi bi-shield-lock-fill me-1'></i> Privacy Restriction</div>
                         I am strictly prohibited from accessing personal member names or individual giving records.";
            }

            string safeFinancialJson = "N/A - General Conversation";
            if (!filter.IsGeneralChat)
            {
                // 2. Route to the correct "Brain" based on what the AI decided
                if (filter.QueryType == "DashboardSummary")
                {
                    safeFinancialJson = await GetDashboardSnapshotAsync(filter);
                }
                else
                {
                    safeFinancialJson = await ExecuteDynamicSearchAsync(filter);
                }
            }

            // 3. Generate final conversational response
            string finalAnswer = await GenerateFinalAiResponseAsync(userQuestion, safeFinancialJson, chatHistory);

            AppendToHistory("User", userQuestion);
            AppendToHistory("Assistant", finalAnswer);

            return finalAnswer;
        }

        // user intent parser (AI to C# backend)
        private async Task<FinancialFilter> ParseUserIntentAsync(string userQuestion, string chatHistory)
        {
            string apiUrl = "https://api.groq.com/openai/v1/chat/completions";

            var requestBody = new
            {
                model = "llama-3.3-70b-versatile",
                messages = new[] {
                    new { role = "system", content = $@"Return ONLY JSON. Today: {DateTime.Today:MMMM d, yyyy}.
                         History: {chatHistory}
                         Extract the user's intent into this exact JSON format:
                         {{
                            ""QueryType"": ""DashboardSummary"" | ""DynamicSearch"",
                            ""StartDate"": ""yyyy-MM-dd"" | null,
                            ""EndDate"": ""yyyy-MM-dd"" | null,
                            ""RecordCode"": ""string"" | null,
                            ""SpecificFund"": ""string"" | null,
                            ""SpecificCategory"": ""string"" | null,
                            ""IsGeneralChat"": bool,
                            ""IsSensitiveQuery"": bool
                         }}
                         
                         RULES:
                         1. Use 'DashboardSummary' if they ask for total balances, cash on hand, or a general monthly/all-time summary.
                         2. Use 'DynamicSearch' if they ask for specific dates, specific categories, specific records, or the 'latest'/'last'/'most recent' transactions.
                         3. StartDate/EndDate: Use 'yyyy-MM-dd'. If they ask for a specific day like 'Jan 4', set BOTH to '2026-01-04'.
                         4. SpecificCategory: Look for 'Solomon', 'Noah', 'Mission', 'Tithes', 'Offerings'.
                         5. Leave fields null if the user does not explicitly mention them.
                         6. CRITICAL PRIVACY: 'Solomon', 'Noah', and 'Mission' are valid categories, NOT member names. DO NOT flag as sensitive!" },
                    new { role = "user", content = userQuestion }
                },
                response_format = new { type = "json_object" },
                temperature = 0.1
            };

            var request = new HttpRequestMessage(HttpMethod.Post, apiUrl);
            request.Headers.Add("Authorization", $"Bearer {_groqApiKey}");
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return new FinancialFilter { IsGeneralChat = true };

            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            string json = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";
            return JsonSerializer.Deserialize<FinancialFilter>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new FinancialFilter();
        }

        private async Task<string> ExecuteDynamicSearchAsync(FinancialFilter filter)
        {
            await _supabase.InitializeAsync(true);

            // 1. DYNAMIC INCOME SEARCH 
            var incQuery = _supabase.Client.From<GivingRecord>().Select("*");

            if (!string.IsNullOrEmpty(filter.StartDate)) incQuery = incQuery.Filter("service_date", Operator.GreaterThanOrEqual, filter.StartDate);
            if (!string.IsNullOrEmpty(filter.EndDate)) incQuery = incQuery.Filter("service_date", Operator.LessThanOrEqual, filter.EndDate);
            if (!string.IsNullOrEmpty(filter.RecordCode)) incQuery = incQuery.Filter("record_code", Operator.Equals, filter.RecordCode);

            var incResp = await incQuery.Get();
            var incRecords = incResp.Models ?? new List<GivingRecord>();
            var incRecordIds = incRecords.Select(r => (object)r.Id).ToList();

            decimal totalIncomeFound = 0;

            // 🚨 NEW: Variables to track the exact breakdown of the income found!
            decimal sumTithes = 0, sumOfferings = 0, sumSolomon = 0, sumNoah = 0, sumMission = 0, sumOthers = 0;

            if (incRecordIds.Any())
            {
                var entriesResp = await _supabase.Client.From<GivingEntry>().Filter("giving_record_id", Operator.In, incRecordIds).Get();
                var entries = entriesResp.Models ?? new List<GivingEntry>();

                foreach (var e in entries)
                {
                    // Track breakdowns so the AI can see them
                    sumTithes += e.Tithes;
                    sumOfferings += e.Offerings;
                    sumSolomon += e.Solomon;
                    sumNoah += e.Noah;
                    sumMission += e.Mission;
                    sumOthers += e.Others;

                    if (!string.IsNullOrEmpty(filter.SpecificCategory))
                    {
                        string cat = filter.SpecificCategory.ToLower();
                        if (cat.Contains("solomon")) totalIncomeFound += e.Solomon;
                        if (cat.Contains("noah")) totalIncomeFound += e.Noah;
                        if (cat.Contains("mission")) totalIncomeFound += e.Mission;
                        if (cat.Contains("tithes")) totalIncomeFound += e.Tithes;
                        if (cat.Contains("offerings")) totalIncomeFound += e.Offerings;
                        continue;
                    }

                    if (!string.IsNullOrEmpty(filter.SpecificFund))
                    {
                        string fund = filter.SpecificFund.ToLower();
                        if (fund.Contains("general") && (e.OthersFund == "General" || string.IsNullOrWhiteSpace(e.OthersFund)))
                            totalIncomeFound += (e.Tithes + e.Offerings + e.Others);
                        else if (fund.Contains("pledge") && e.OthersFund == "Pledges")
                            totalIncomeFound += (e.Solomon + e.Noah + e.Mission + e.Others);
                        else if (fund.Contains("construction") && e.OthersFund == "Construction")
                            totalIncomeFound += e.Others;
                        else if (fund.Contains("praise") && e.OthersFund == "Praise & Worship")
                            totalIncomeFound += e.Others;
                        continue;
                    }
                    totalIncomeFound += (e.Tithes + e.Offerings + e.Solomon + e.Noah + e.Mission + e.Others);
                }
            }

            // 2. DYNAMIC EXPENSE SEARCH 
            var expQuery = _supabase.Client.From<DisbursementRecord>().Select("*");
            if (!string.IsNullOrEmpty(filter.StartDate)) expQuery = expQuery.Filter("record_date", Operator.GreaterThanOrEqual, filter.StartDate);
            if (!string.IsNullOrEmpty(filter.EndDate)) expQuery = expQuery.Filter("record_date", Operator.LessThanOrEqual, filter.EndDate);
            if (!string.IsNullOrEmpty(filter.RecordCode))
            {
                if (incRecordIds.Any())
                {
                    expQuery = expQuery.Filter("giving_record_id", Operator.In, incRecordIds);
                }
                else
                {
                    expQuery = expQuery.Filter("id", Operator.Equals, -1);
                }
            }
            var expResp = await expQuery.Get();
            var expRecords = expResp.Models ?? new List<DisbursementRecord>();
            var expRecordIds = expRecords.Select(r => (object)r.Id).ToList();

            decimal totalExpenseFound = 0;

            // 🚨 NEW: List to track every specific expense item
            var expenseList = new List<dynamic>();

            if (expRecordIds.Any()) 
            { 
                var vResp = await _supabase.Client.From<Voucher>().Filter("disbursement_record_id", Operator.In, expRecordIds).Get();
                var vouchers = vResp.Models ?? new List<Voucher>();
                var voucherIds = vouchers.Select(v => (object)v.Id).ToList();

                if (voucherIds.Any())
                {
                    var iResp = await _supabase.Client.From<VoucherItem>().Filter("voucher_id", Operator.In, voucherIds).Get();
                    var items = iResp.Models ?? new List<VoucherItem>();

                    foreach (var i in items)
                    {
                        decimal netExp = i.Amount - i.AmountReturned;
                        if (netExp <= 0) continue;

                        string fundSource = i.FundSource ?? "General";
                        if (!string.IsNullOrEmpty(filter.SpecificFund) && !fundSource.ToLower().Contains(filter.SpecificFund.ToLower().Replace(" fund", "")))
                            continue;

                        totalExpenseFound += netExp;

                        // Link back to the parent voucher to grab the Ministry
                        var parentVoucher = vouchers.FirstOrDefault(v => v.Id == i.VoucherId);
                        expenseList.Add(new
                        {
                            Ministry = parentVoucher?.Ministry ?? "Uncategorized",
                            ItemName = i.Particular ?? "Miscellaneous",
                            Amount = netExp
                        });
                    }
                }
            }

            // 🚨 NEW: Group the expenses for the AI so it doesn't get overwhelmed reading 100s of rows
            var ministryBreakdown = expenseList
                .GroupBy(x => x.Ministry)
                .Select(g => new { Ministry = g.Key, Total = g.Sum(x => (decimal)x.Amount) })
                .OrderByDescending(x => x.Total)
                .ToList();

            var largestItems = expenseList
                .OrderByDescending(x => (decimal)x.Amount)
                .Take(5) // Just give the AI the top 5 largest items
                .ToList();

            // 3. RETURN RESULTS 
            var latestInc = incRecords.OrderByDescending(r => r.ServiceDate).FirstOrDefault();
            var latestExp = expRecords.OrderByDescending(r => r.RecordDate).FirstOrDefault();

            return JsonSerializer.Serialize(new
            {
                Context = "DYNAMIC SEARCH RESULTS (Ad-Hoc Query)",
                FiltersApplied = new
                {
                    DatesSearched = $"{filter.StartDate ?? "Any"} to {filter.EndDate ?? "Any"}",
                    RecordCode = filter.RecordCode ?? "None",
                    SpecificCategory = filter.SpecificCategory ?? "None",
                    SpecificFund = filter.SpecificFund ?? "All"
                },
                Results = new
                {
                    IncomeFound = totalIncomeFound,
                    // Pass the income breakdown directly to the AI
                    IncomeBreakdown = new
                    {
                        Tithes = sumTithes,
                        Offerings = sumOfferings,
                        Solomon = sumSolomon,
                        Noah = sumNoah,
                        Mission = sumMission,
                        Others = sumOthers
                    },
                    ExpenseFound = totalExpenseFound,
                    // Pass the expense groupings directly to the AI
                    MinistryExpenses = ministryBreakdown,
                    LargestExpenseItems = largestItems,

                    NetMatched = totalIncomeFound - totalExpenseFound,
                    DatabaseRowsScanned = incRecords.Count + expRecords.Count,
                    LatestIncomeRecordDate = latestInc?.ServiceDate.ToString("MMMM dd, yyyy") ?? "None",
                    LatestIncomeRecordCode = latestInc?.RecordCode ?? "None",
                    LatestExpenseRecordDate = latestExp?.RecordDate.ToString("MMMM dd, yyyy") ?? "None"
                }
            });
        }

        // dashboard logic replica
        private async Task<string> GetDashboardSnapshotAsync(FinancialFilter filter)
        {
            await _supabase.InitializeAsync(true);

            // Use current month if no dates provided
            DateTime targetMonth = string.IsNullOrEmpty(filter.StartDate) ? DateTime.Today : DateTime.ParseExact(filter.StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            string targetMonthStr = targetMonth.ToString("yyyy-MM");
            DateTime firstDay = new DateTime(targetMonth.Year, targetMonth.Month, 1);
            DateTime lastDay = firstDay.AddMonths(1).AddDays(-1);

            // Determine if it's "All Time"
            bool isAllTime = string.IsNullOrEmpty(filter.StartDate) && string.IsNullOrEmpty(filter.SpecificFund) && string.IsNullOrEmpty(filter.SpecificCategory);

            var mfbResp = await _supabase.Client.From<MonthlyFundBalance>().Get();
            var allBalances = mfbResp.Models ?? new List<MonthlyFundBalance>();
            var loansResp = await _supabase.Client.From<InterFundLoan>().Filter("status", Operator.Equals, "Active").Get();
            var activeLoans = loansResp.Models ?? new List<InterFundLoan>();

            decimal begGen = 0, begPledges = 0, begConst = 0, begPW = 0;

            if (!isAllTime)
            {
                var lastCheckpoint = allBalances.Where(b => string.Compare(b.ReportMonth, targetMonthStr) < 0).OrderByDescending(b => b.ReportMonth).FirstOrDefault();
                DateTime gapStart = new DateTime(2000, 1, 1);
                if (lastCheckpoint != null)
                {
                    string cpMonth = lastCheckpoint.ReportMonth;
                    begGen = allBalances.FirstOrDefault(b => b.ReportMonth == cpMonth && b.FundName == "General")?.EndingBalance ?? 0;
                    begPledges = allBalances.FirstOrDefault(b => b.ReportMonth == cpMonth && b.FundName == "Pledges")?.EndingBalance ?? 0;
                    begConst = allBalances.FirstOrDefault(b => b.ReportMonth == cpMonth && b.FundName == "Construction")?.EndingBalance ?? 0;
                    begPW = allBalances.FirstOrDefault(b => b.ReportMonth == cpMonth && b.FundName == "Praise & Worship")?.EndingBalance ?? 0;
                    gapStart = DateTime.ParseExact(cpMonth + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture).AddMonths(1);
                }

                DateTime gapEnd = firstDay.AddDays(-1);
                if (gapStart <= gapEnd)
                {
                    var (gGen, gPledges, gConst, gPW) = await CalculateIncomeRangeAsync(gapStart, gapEnd);
                    var (eGen, ePledges, eConst, ePW) = await CalculateExpenseRangeAsync(gapStart, gapEnd);
                    begGen += (gGen - eGen); begPledges += (gPledges - ePledges);
                    begConst += (gConst - eConst); begPW += (gPW - ePW);
                }
            }

            DateTime liveStart = isAllTime ? new DateTime(2000, 1, 1) : firstDay;
            DateTime liveEnd = isAllTime ? DateTime.Today : lastDay;

            var (incGen, incPledges, incConst, incPW) = await CalculateIncomeRangeAsync(liveStart, liveEnd);
            var (expGen, expPledges, expConst, expPW) = await CalculateExpenseRangeAsync(liveStart, liveEnd);

            decimal genBorrowed = activeLoans.Where(l => l.BorrowerFund == "General").Sum(l => l.RemainingBalance);
            decimal pledgesLent = activeLoans.Where(l => l.LenderFund == "Pledges").Sum(l => l.RemainingBalance);
            decimal constLent = activeLoans.Where(l => l.LenderFund == "Construction").Sum(l => l.RemainingBalance);
            decimal pwLent = activeLoans.Where(l => l.LenderFund == "Praise & Worship").Sum(l => l.RemainingBalance);

            return JsonSerializer.Serialize(new
            {
                Context = isAllTime ? "FULL DASHBOARD SUMMARY (LIFETIME)" : $"DASHBOARD SUMMARY ({targetMonthStr})",
                GrandDashboardTotal_CashOnHand = (begGen + incGen - expGen + genBorrowed) + (begPledges + incPledges - expPledges - pledgesLent) + (begConst + incConst - expConst - constLent) + (begPW + incPW - expPW - pwLent),
                Wallets = new[]
                {
                    new { Name = "General Fund", BookBalance = begGen + incGen - expGen, ActualCashOnHand = (begGen + incGen - expGen) + genBorrowed },
                    new { Name = "Pledges Fund", BookBalance = begPledges + incPledges - expPledges, ActualCashOnHand = (begPledges + incPledges - expPledges) - pledgesLent },
                    new { Name = "Construction Fund", BookBalance = begConst + incConst - expConst, ActualCashOnHand = (begConst + incConst - expConst) - constLent },
                    new { Name = "Praise & Worship Fund", BookBalance = begPW + incPW - expPW, ActualCashOnHand = (begPW + incPW - expPW) - pwLent }
                }
            });
        }

        // Helper calculations for Dashboard Math
        private async Task<(decimal gen, decimal pledges, decimal cons, decimal pw)> CalculateIncomeRangeAsync(DateTime start, DateTime end)
        {
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
            return (gen, pledges, cons, pw);
        }

        private async Task<(decimal gen, decimal pledges, decimal cons, decimal pw)> CalculateExpenseRangeAsync(DateTime start, DateTime end)
        {
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
            return (gen, pledges, cons, pw);
        }

        // 🗣️ THE TRANSLATOR (Final Output)
        private async Task<string> GenerateFinalAiResponseAsync(string userQuestion, string jsonContext, string chatHistory)
        {
            string apiUrl = "https://api.groq.com/openai/v1/chat/completions";

            string systemPrompt = $@"
                You are the intelligent, professional ACC Financial Assistant. 
                Your job is to analyze the database results provided and answer the user clearly.

                =========================================
                📊 DATABASE RESULTS (Source of Truth)
                =========================================
                {jsonContext}
                
                Note: 
                - If the context says 'DASHBOARD SUMMARY', you are looking at full wallet balances.
                - If the context says 'DYNAMIC SEARCH RESULTS', you are looking at specific filtered records (like a specific date, code, or category). Only report the 'IncomeFound' or 'ExpenseFound' from the filters applied.

                =========================================
                💬 RECENT CHAT HISTORY
                =========================================
                {chatHistory}

               =========================================
                🛑 STRICT RULES FOR YOUR RESPONSE (ANTI-YAP PROTOCOL)
                =========================================
                1. BE EXTREMELY CONCISE: Use the absolute minimum number of words necessary to answer the prompt.
                2. ZERO FILLER WORDS: NEVER start sentences with ""Based on the database results..."" or ""According to the provided data..."". Do not greet the user again. Just instantly provide the answer.
                3. NO MARKDOWN: NEVER use markdown formatting (do not use **, *, or #). 
                4. HTML ONLY: You MUST use clean HTML for formatting. Use <b> for bolding numbers, <br><br> for paragraph breaks, and <ul><li> for making clean lists.
                5. CURRENCY: ALL currency must use the Philippine Peso sign (₱). NEVER use the dollar sign ($). Example: <b>₱ 46,049.55</b>.
                6. PRIVACY LOCK: You are strictly prohibited from mentioning individual member names or personal donor amounts.
                7. ROLE ENFORCEMENT: The user is the ACC Financial Auditor. You are the AI Assistant. Never claim to be the auditor.
                
            ";

            var requestBody = new
            {
                model = "llama-3.3-70b-versatile",
                messages = new[] {
                    new { role = "system", content = systemPrompt }, 
                    new { role = "user", content = userQuestion }
                },
                temperature = 0.3
            };

            var request = new HttpRequestMessage(HttpMethod.Post, apiUrl);
            request.Headers.Add("Authorization", $"Bearer {_groqApiKey}");
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        }
    }

    // NEW DYNAMIC FILTER CLASS 
    public class FinancialFilter
    {
        public string QueryType { get; set; } = "DashboardSummary";
        public string? StartDate { get; set; }
        public string? EndDate { get; set; }
        public string? RecordCode { get; set; }
        public string? SpecificFund { get; set; }
        public string? SpecificCategory { get; set; }

        public bool IsSensitiveQuery { get; set; } = false;
        public bool IsGeneralChat { get; set; } = false;
    }
}