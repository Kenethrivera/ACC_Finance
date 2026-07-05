using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using acc_finance.Models;
using acc_finance.Services;
using Microsoft.AspNetCore.Authorization;
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

        public IndexModel(SupabaseService supabase)
        {
            _supabase = supabase;
        }

        [BindProperty(SupportsGet = true)]
        public string Filter { get; set; } = "COH"; // Default tab

        public List<CashOnHandSnapshot> CohSnapshots { get; set; } = new();
        public List<WalletLedgerEntry> WalletEntries { get; set; } = new();
        public string ErrorMessage { get; set; } = "";

        public async Task<IActionResult> OnGetAsync()
        {
            if (!User.Identity?.IsAuthenticated ?? true) return RedirectToPage("/Login");

            try
            {
                await _supabase.InitializeAsync(true);

                // 1. Fetch all data into memory for fast Gap Math calculation
                var mfbResp = await _supabase.Client.From<MonthlyFundBalance>().Get();
                var allBalances = mfbResp.Models ?? new List<MonthlyFundBalance>();

                var givingRecordsResp = await _supabase.Client.From<GivingRecord>().Get();
                var allGivingRecords = givingRecordsResp.Models ?? new List<GivingRecord>();

                var givingEntriesResp = await _supabase.Client.From<GivingEntry>().Get();
                var allGivingEntries = givingEntriesResp.Models ?? new List<GivingEntry>();

                var disbRecordsResp = await _supabase.Client.From<DisbursementRecord>().Get();
                var allDisbRecords = disbRecordsResp.Models ?? new List<DisbursementRecord>();

                var vouchersResp = await _supabase.Client.From<Voucher>().Get();
                var allVouchers = vouchersResp.Models ?? new List<Voucher>();

                var voucherItemsResp = await _supabase.Client.From<VoucherItem>().Get();
                var allVoucherItems = voucherItemsResp.Models ?? new List<VoucherItem>();

                if (Filter == "COH")
                {
                    GenerateCohSnapshots(allBalances, allGivingRecords, allGivingEntries, allDisbRecords, allVouchers, allVoucherItems);
                }
                else
                {
                    GenerateWalletLedger(Filter, allGivingRecords, allGivingEntries, allDisbRecords, allVouchers, allVoucherItems);
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = "System Error fetching ledger data: " + ex.Message;
            }

            return Page();
        }

        private void GenerateCohSnapshots(
            List<MonthlyFundBalance> allBalances,
            List<GivingRecord> allGivingRecords, List<GivingEntry> allGivingEntries,
            List<DisbursementRecord> allDisbRecords, List<Voucher> allVouchers, List<VoucherItem> allVoucherItems)
        {
            // Extract all unique dates to group by month
            var allDates = allGivingRecords.Select(g => g.ServiceDate.Date)
                                        .Concat(allDisbRecords.Select(d => d.RecordDate.Date))
                                        .Distinct()
                                        .ToList();

            var yearMonths = allDates.GroupBy(d => new { d.Year, d.Month })
                                     .OrderByDescending(g => g.Key.Year)
                                     .ThenByDescending(g => g.Key.Month)
                                     .ToList();

            foreach (var ym in yearMonths)
            {
                DateTime maxDate = ym.Max();
                string targetMonthStr = maxDate.ToString("yyyy-MM");

                // EXACT DASHBOARD GAP MATH LOGIC
                var applicableBalances = allBalances
                    .Where(b => string.Compare(b.ReportMonth, targetMonthStr) <= 0) // Find checkpoint before or equal to this month
                    .OrderByDescending(b => b.ReportMonth)
                    .ToList();

                string lastCheckpointMonth = applicableBalances.FirstOrDefault()?.ReportMonth;
                decimal begGen = 0, begPledges = 0, begConst = 0, begPW = 0;
                DateTime gapStart = new DateTime(2000, 1, 1);

                if (!string.IsNullOrEmpty(lastCheckpointMonth))
                {
                    begGen = applicableBalances.FirstOrDefault(b => b.ReportMonth == lastCheckpointMonth && b.FundName == "General")?.EndingBalance ?? 0;
                    begPledges = applicableBalances.FirstOrDefault(b => b.ReportMonth == lastCheckpointMonth && b.FundName == "Pledges")?.EndingBalance ?? 0;
                    begConst = applicableBalances.FirstOrDefault(b => b.ReportMonth == lastCheckpointMonth && b.FundName == "Construction")?.EndingBalance ?? 0;
                    begPW = applicableBalances.FirstOrDefault(b => b.ReportMonth == lastCheckpointMonth && b.FundName == "Praise & Worship")?.EndingBalance ?? 0;

                    gapStart = DateTime.ParseExact(lastCheckpointMonth + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture).AddMonths(1);
                }

                DateTime gapEnd = maxDate.Date;
                decimal incGen = 0, incPledges = 0, incConst = 0, incPW = 0;
                decimal expGen = 0, expPledges = 0, expConst = 0, expPW = 0;

                if (gapStart <= gapEnd)
                {
                    // Calculate Income for Gap
                    var gapGivings = allGivingRecords.Where(g => g.ServiceDate.Date >= gapStart && g.ServiceDate.Date <= gapEnd).ToList();
                    var gapGivingIds = gapGivings.Select(g => g.Id).ToHashSet();
                    var gapEntries = allGivingEntries.Where(e => gapGivingIds.Contains(e.GivingRecordId)).ToList();

                    incPledges = gapEntries.Sum(e => e.Solomon + e.Noah + e.Mission) + gapEntries.Where(e => string.Equals(e.OthersFund, "Pledges", StringComparison.OrdinalIgnoreCase)).Sum(e => e.Others);
                    incGen = gapEntries.Sum(e => e.Tithes + e.Offerings) + gapEntries.Where(e => string.Equals(e.OthersFund, "General", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(e.OthersFund)).Sum(e => e.Others);
                    incConst = gapEntries.Where(e => string.Equals(e.OthersFund, "Construction", StringComparison.OrdinalIgnoreCase)).Sum(e => e.Others);
                    incPW = gapEntries.Where(e => string.Equals(e.OthersFund, "Praise & Worship", StringComparison.OrdinalIgnoreCase)).Sum(e => e.Others);

                    // Calculate Expenses for Gap
                    var gapDisbs = allDisbRecords.Where(d => d.RecordDate.Date >= gapStart && d.RecordDate.Date <= gapEnd).ToList();
                    var gapDisbIds = gapDisbs.Select(d => d.Id).ToHashSet();
                    var gapVouchers = allVouchers.Where(v => gapDisbIds.Contains(v.DisbursementRecordId)).ToList();
                    var gapVoucherIds = gapVouchers.Select(v => v.Id).ToHashSet();
                    var gapItems = allVoucherItems.Where(i => gapVoucherIds.Contains(i.VoucherId)).ToList();

                    foreach (var item in gapItems)
                    {
                        decimal netExp = item.Amount - item.AmountReturned;
                        if (netExp <= 0) continue;

                        string fund = item.FundSource?.Trim() ?? "General";
                        if (string.Equals(fund, "Construction", StringComparison.OrdinalIgnoreCase)) expConst += netExp;
                        else if (string.Equals(fund, "Praise & Worship", StringComparison.OrdinalIgnoreCase)) expPW += netExp;
                        else if (string.Equals(fund, "Pledges", StringComparison.OrdinalIgnoreCase)) expPledges += netExp;
                        else expGen += netExp;
                    }
                }

                // TRUE LEDGER BALANCES
                decimal genBal = begGen + incGen - expGen;
                decimal pledgesBal = begPledges + incPledges - expPledges;
                decimal constBal = begConst + incConst - expConst;
                decimal pwBal = begPW + incPW - expPW;

                // CUSTODIAN & RECONCILIATION MATH
                decimal sisCoraTotal = genBal + pledgesBal; // Sis Cora handles both General & Pledges
                decimal pledgesActual = genBal < 0 ? (genBal + pledgesBal) : pledgesBal; // If general is negative, it eats into Pledges cash

                CohSnapshots.Add(new CashOnHandSnapshot
                {
                    Date = maxDate,
                    TotalCohAmount = sisCoraTotal + constBal + pwBal,

                    CoSisCora = sisCoraTotal,
                    CoPtraEs = constBal,
                    CoPW = pwBal,

                    PledgesShouldBeTotal = pledgesBal,
                    PledgesActualTotal = pledgesActual
                    // Note: ShouldReturn is computed via property: PledgesShouldBeTotal - PledgesActualTotal
                });
            }
        }

        private void GenerateWalletLedger(
            string walletType,
            List<GivingRecord> givingRecords, List<GivingEntry> givingEntries,
            List<DisbursementRecord> disbRecords, List<Voucher> vouchers, List<VoucherItem> voucherItems)
        {
            var ledger = new List<WalletLedgerEntry>();
            decimal runningBalance = 0;

            // Process Inflows
            var inflows = new List<WalletLedgerEntry>();
            foreach (var entry in givingEntries)
            {
                var record = givingRecords.FirstOrDefault(r => r.Id == entry.GivingRecordId);
                if (record == null) continue;

                decimal amount = 0;
                if (walletType == "General")
                {
                    amount = entry.Tithes + entry.Offerings;
                    if (string.Equals(entry.OthersFund, "General", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(entry.OthersFund))
                    {
                        amount += entry.Others;
                    }
                }
                else if (walletType == "Pledges")
                {
                    amount = entry.Solomon + entry.Noah + entry.Mission;
                    if (string.Equals(entry.OthersFund, "Pledges", StringComparison.OrdinalIgnoreCase)) amount += entry.Others;
                }
                else if (string.Equals(entry.OthersFund, walletType, StringComparison.OrdinalIgnoreCase))
                {
                    amount = entry.Others;
                }

                if (amount > 0)
                {
                    inflows.Add(new WalletLedgerEntry
                    {
                        Date = record.ServiceDate,
                        Description = $"Inflow - {record.RecordCode}",
                        Amount = amount
                    });
                }
            }

            // Process Outflows
            var outflows = new List<WalletLedgerEntry>();
            foreach (var item in voucherItems)
            {
                string fund = string.IsNullOrWhiteSpace(item.FundSource) ? "General" : item.FundSource;
                if (!string.Equals(fund, walletType, StringComparison.OrdinalIgnoreCase)) continue;

                var voucher = vouchers.FirstOrDefault(v => v.Id == item.VoucherId);
                if (voucher == null) continue;

                var disbRecord = disbRecords.FirstOrDefault(dr => dr.Id == voucher.DisbursementRecordId);
                if (disbRecord == null) continue;

                decimal netOutflow = item.Amount - item.AmountReturned;
                if (netOutflow > 0)
                {
                    outflows.Add(new WalletLedgerEntry
                    {
                        Date = disbRecord.RecordDate,
                        Description = $"Voucher {voucher.VoucherNumber} - {item.Particular}",
                        Amount = -netOutflow
                    });
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
    }
}