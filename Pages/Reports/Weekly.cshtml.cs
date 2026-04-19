using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using static Supabase.Postgrest.Constants;
using acc_finance.Models;
using acc_finance.Models.Reports;
using acc_finance.Services;

namespace acc_finance.Pages.Reports
{
    public class WeeklyModel : PageModel
    {
        private readonly SupabaseService _supabase;

        public WeeklyModel(SupabaseService supabase)
        {
            _supabase = supabase;
        }

        [BindProperty(SupportsGet = true)]
        public DateTime ReportDate { get; set; } = DateTime.Today;

        public WeeklyFinancialReportVm Report { get; set; } = new();

        public string Message { get; set; } = "";

        public async Task OnGetAsync()
        {
            await _supabase.InitializeAsync(true);

            var safeDate = ReportDate.Date.AddHours(12);
            string dateOnly = safeDate.ToString("yyyy-MM-dd");

            Report = new WeeklyFinancialReportVm
            {
                ReportDate = safeDate
            };

            // =========================
            // 1. LOAD GIVING RECORD
            // =========================
            var givingRecordResponse = await _supabase.Client
                .From<GivingRecord>()
                .Filter("service_date", Operator.Equals, dateOnly)
                .Get();

            var givingRecord = givingRecordResponse.Models.FirstOrDefault();

            if (givingRecord == null)
            {
                Message = "No giving record found for the selected date.";
                return;
            }

            Report.Giving.GivingRecordId = givingRecord.Id;
            Report.Giving.RecordCode = givingRecord.RecordCode;
            Report.Giving.ServiceDate = givingRecord.ServiceDate;
            Report.Giving.TotalTithes = givingRecord.TotalTithes;
            Report.Giving.TotalOfferings = givingRecord.TotalOfferings;
            Report.Giving.TotalSolomon = givingRecord.TotalSolomon;
            Report.Giving.TotalNoah = givingRecord.TotalNoah;
            Report.Giving.TotalMission = givingRecord.TotalMission;
            Report.Giving.TotalOthers = givingRecord.TotalOthers;
            Report.Giving.GrandTotal = givingRecord.GrandTotal;

            // =========================
            // 2. LOAD MEMBERS
            // =========================
            var memberResponse = await _supabase.Client
                .From<Member>()
                .Get();

            var members = memberResponse.Models.ToList();

            // =========================
            // 3. LOAD GIVING ENTRIES
            // =========================
            var givingEntryResponse = await _supabase.Client
                .From<GivingEntry>()
                .Filter("giving_record_id", Operator.Equals, givingRecord.Id.ToString())
                .Get();

            var givingEntries = givingEntryResponse.Models.ToList();

            Report.Giving.Rows = givingEntries
    .Select(e =>
    {
        string name = e.MemberId.HasValue
            ? members.FirstOrDefault(m => m.Id == e.MemberId.Value)?.Name ?? "(Unknown Member)"
            : e.EntryName ?? "(Unnamed)";

        string lower = name.ToLower();

        bool isAnonymous = lower.Contains("anonymous");
        bool isGroup =
            lower.Contains("kids") ||
            lower.Contains("prayer") ||
            lower.Contains("youth") ||
            lower.Contains("camp") ||
            lower.Contains("meeting") ||
            lower.Contains("group");

        bool isFamily = name.Contains("&");

        bool isOthersOnly =
            e.Tithes == 0 &&
            e.Offerings == 0 &&
            e.Solomon == 0 &&
            e.Noah == 0 &&
            e.Mission == 0 &&
            e.Others > 0;

        int sortGroup = 0;

        if (isOthersOnly)
            sortGroup = 5;
        else if (isAnonymous)
            sortGroup = 4;
        else if (isGroup)
            sortGroup = 3;
        else if (isFamily)
            sortGroup = 2;
        else
            sortGroup = 1;

        return new GivingReportRowVm
        {
            MemberId = e.MemberId,
            Name = name,
            Tithes = e.Tithes,
            Offerings = e.Offerings,
            Solomon = e.Solomon,
            Noah = e.Noah,
            Mission = e.Mission,
            Others = e.Others,
            Total = e.Total,
            IsFamily = isFamily,
            IsGroup = isGroup,
            SortGroup = sortGroup
        };
    })
    .OrderBy(x => x.SortGroup)
    .ThenBy(x => x.Name)
    .ToList();

            // =========================
            // 4. LOAD DENOMINATION
            // =========================
            var denominationResponse = await _supabase.Client
                .From<GivingDenomination>()
                .Filter("giving_record_id", Operator.Equals, givingRecord.Id.ToString())
                .Get();

            var denomination = denominationResponse.Models.FirstOrDefault();

            if (denomination != null)
            {
                Report.Denomination.Exists = true;
                Report.Denomination.Lines = BuildDenominationLines(denomination);
                Report.Denomination.Total = denomination.Total;
            }

            // =========================
            // 5. LOAD DISBURSEMENT RECORD
            // =========================
            var disbursementRecordResponse = await _supabase.Client
                .From<DisbursementRecord>()
                .Filter("giving_record_id", Operator.Equals, givingRecord.Id.ToString())
                .Get();

            var disbursementRecord = disbursementRecordResponse.Models.FirstOrDefault();

            if (disbursementRecord != null)
            {
                Report.Disbursement.DisbursementRecordId = disbursementRecord.Id;
                Report.Disbursement.RecordDate = disbursementRecord.RecordDate;
                Report.Disbursement.TotalReleased = disbursementRecord.TotalReleased;
                Report.Disbursement.TotalReturned = disbursementRecord.TotalReturned;
                Report.Disbursement.TotalNetDisbursement =
                    disbursementRecord.TotalReleased - disbursementRecord.TotalReturned;

                var voucherResponse = await _supabase.Client
                    .From<Voucher>()
                    .Filter("disbursement_record_id", Operator.Equals, disbursementRecord.Id.ToString())
                    .Get();

                var vouchers = voucherResponse.Models.ToList();

                var voucherItemResponse = await _supabase.Client
                    .From<VoucherItem>()
                    .Get();

                var allVoucherItems = voucherItemResponse.Models.ToList();

                
                // 5A. BUILD CASH DISBURSEMENT REPORT (GROUP BY VOUCHER)
                Report.Disbursement.Vouchers = vouchers
                    .OrderBy(v =>
                    {
                        int parsed;
                        return int.TryParse(v.VoucherNumber, out parsed) ? parsed : int.MaxValue;
                    })
                    .ThenBy(v => v.VoucherNumber)
                    .Select(voucher =>
                    {
                        var items = allVoucherItems
                            .Where(i => i.VoucherId == voucher.Id)
                            .OrderBy(i => i.Id)
                            .Select(item => new DisbursementLineVm
                            {
                                VoucherNumber = voucher.VoucherNumber,
                                Payee = voucher.Payee,
                                Particular = item.Particular,
                                AmountReleased = item.Amount,
                                CashReturned = item.AmountReturned,
                                NetAmount = item.Amount - item.AmountReturned,
                                SummaryLabel = BuildSummaryLabel(item.Particular, voucher.Payee)
                            })
                            .ToList();

                        return new DisbursementVoucherVm
                        {
                            VoucherNumber = voucher.VoucherNumber,
                            Ministry = voucher.Ministry,
                            Payee = voucher.Payee,
                            Lines = items,
                            VoucherTotalReleased = items.Sum(x => x.AmountReleased),
                            VoucherTotalReturned = items.Sum(x => x.CashReturned),
                            VoucherNetAmount = items.Sum(x => x.NetAmount)
                        };
                    })
                    .ToList();

                // 5B. BUILD SUMMARY DISBURSEMENT (GROUP BY MINISTRY)
                Report.Disbursement.Groups = Report.Disbursement.Vouchers
    .GroupBy(v => v.Ministry)
    .Select(group =>
    {
        var orderedVouchers = group
            .OrderBy(v =>
            {
                int parsed;
                return int.TryParse(v.VoucherNumber, out parsed) ? parsed : int.MaxValue;
            })
            .ThenBy(v => v.VoucherNumber)
            .ToList();

        var lines = orderedVouchers
            .SelectMany(v => v.Lines)
            .ToList();

        var firstVoucherNumber = orderedVouchers.FirstOrDefault()?.VoucherNumber ?? "";

        return new DisbursementGroupVm
        {
            Ministry = group.Key,
            Lines = lines,
            GroupTotal = lines.Sum(x => x.NetAmount),
            SortVoucherNumber = firstVoucherNumber
        };
    })
    .OrderBy(x =>
    {
        int parsed;
        return int.TryParse(x.SortVoucherNumber, out parsed) ? parsed : int.MaxValue;
    })
    .ThenBy(x => x.SortVoucherNumber)
    .ToList();
            }

            // =========================
            // 6. BUILD SUMMARY
            // =========================
            Report.Summary.CashReceiptsOrBlessings = Report.Giving.GrandTotal;
            Report.Summary.LessCashDisbursements = Report.Disbursement.TotalNetDisbursement;
            Report.Summary.NetCashBalance =
                Report.Summary.CashReceiptsOrBlessings - Report.Summary.LessCashDisbursements;
        }

        private List<DenominationLineVm> BuildDenominationLines(GivingDenomination d)
        {
            var lines = new List<DenominationLineVm>();

            AddDenominationLine(lines, "1000", d.Qty1000, 1000m);
            AddDenominationLine(lines, "500", d.Qty500, 500m);
            AddDenominationLine(lines, "200", d.Qty200, 200m);
            AddDenominationLine(lines, "100", d.Qty100, 100m);
            AddDenominationLine(lines, "50", d.Qty50, 50m);
            AddDenominationLine(lines, "20 Coin", d.Qty20Coin, 20m);
            AddDenominationLine(lines, "20 Paper", d.Qty20Paper, 20m);
            AddDenominationLine(lines, "10", d.Qty10, 10m);
            AddDenominationLine(lines, "5", d.Qty5, 5m);
            AddDenominationLine(lines, "1", d.Qty1, 1m);
            AddDenominationLine(lines, "25 Cent", d.Qty25Cent, 0.25m);
            AddDenominationLine(lines, "10 Cent", d.Qty10Cent, 0.10m);
            AddDenominationLine(lines, "5 Cent", d.Qty5Cent, 0.05m);
            AddDenominationLine(lines, "1 Cent", d.Qty1Cent, 0.01m);

            return lines;
        }

        private void AddDenominationLine(List<DenominationLineVm> lines, string label, int qty, decimal unitValue)
        {
            if (qty <= 0)
                return;

            lines.Add(new DenominationLineVm
            {
                Label = label,
                Quantity = qty,
                UnitValue = unitValue,
                LineTotal = qty * unitValue
            });
        }

        private string BuildSummaryLabel(string particular, string payee)
        {
            var cleanParticular = (particular ?? "").Trim();
            var cleanPayee = (payee ?? "").Trim();

            var lower = cleanParticular.ToLower();

            bool includePayee =
                lower.Contains("stipend") ||
                lower.Contains("professional fee");

            if (includePayee && !string.IsNullOrWhiteSpace(cleanPayee))
            {
                return $"{cleanParticular} - {cleanPayee}";
            }

            return cleanParticular;
        }
    }
}