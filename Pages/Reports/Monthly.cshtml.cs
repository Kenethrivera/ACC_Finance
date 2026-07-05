using acc_finance.Models;
using acc_finance.Models.Reports;
using acc_finance.Services;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using System.Globalization;
using System.Text.RegularExpressions;
using static acc_finance.Models.SystemSetup;
using static Supabase.Postgrest.Constants;
using Border = Google.Apis.Sheets.v4.Data.Border;
using CellFormat = Google.Apis.Sheets.v4.Data.CellFormat;
using Color = Google.Apis.Sheets.v4.Data.Color;
using GridRange = Google.Apis.Sheets.v4.Data.GridRange;
using NumberFormat = Google.Apis.Sheets.v4.Data.NumberFormat;
using Request = Google.Apis.Sheets.v4.Data.Request;
using TextFormat = Google.Apis.Sheets.v4.Data.TextFormat;

namespace acc_finance.Pages.Reports
{
    [Authorize]
    public class MonthlyModel : PageModel
    {
        private readonly SupabaseService _supabase;
        private readonly AiAuditorService _aiAuditor;
        private readonly IMemoryCache _cache;
        public MonthlyModel(SupabaseService supabase, AiAuditorService aiAuditor, IMemoryCache cache)
        {
            _supabase = supabase;
            _aiAuditor = aiAuditor;
            _cache = cache;
        }

        [BindProperty(SupportsGet = true)]
        public string SelectedMonth { get; set; } = "";

        [BindProperty]
        public string AiSummary { get; set; }

        public MonthlyFinancialReportVm MonthReport { get; set; } = new();

        public string Message { get; set; } = "";
        public decimal MonthBegBalance { get; set; }
        public decimal MonthTotalReceipts { get; set; }
        public decimal MonthTotalDisbursements { get; set; }
        public decimal MonthNetBalance { get; set; }
        public string PreviousMonthLabel { get; set; }

        [BindProperty]
        public SystemSettingsRecord AppSettings { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            // 🚨 PERSIST LOADED RECORD DATE 🚨
            var qsMonth = Request.Query["SelectedMonth"].ToString();
            if (string.IsNullOrEmpty(qsMonth))
            {
                var savedMonth = HttpContext.Session.GetString("ActiveReportMonth");
                if (!string.IsNullOrEmpty(savedMonth))
                {
                    // Redirect to force the URL to update to the remembered month
                    return RedirectToPage(new { SelectedMonth = savedMonth });
                }
            }
            else
            {
                // Save the currently viewed month to session
                HttpContext.Session.SetString("ActiveReportMonth", SelectedMonth);
            }

            await LoadMonthAsync();
            return Page();
        }

        private async Task LoadMonthAsync()
        {
            await _supabase.InitializeAsync(true);

            var settingsResp = await _supabase.Client.From<SystemSettingsRecord>().Filter("id", Operator.Equals, 1).Get();
            AppSettings = settingsResp.Models.FirstOrDefault() ?? new SystemSettingsRecord();

            // 1. Determine the exact month we are looking for
            if (string.IsNullOrWhiteSpace(SelectedMonth))
            {
                // Check RAM to see if we already know the latest active month across the church
                if (!_cache.TryGetValue("LatestReportMonth", out string latestMonth))
                {
                    var latestGivingResp = await _supabase.Client.From<GivingRecord>()
                        .Select("service_date").Order("service_date", Ordering.Descending).Limit(1).Get();
                    var latestGivingDate = latestGivingResp.Models.FirstOrDefault()?.ServiceDate ?? DateTime.MinValue;

                    var latestDisbResp = await _supabase.Client.From<DisbursementRecord>()
                        .Select("record_date").Order("record_date", Ordering.Descending).Limit(1).Get();
                    var latestDisbDate = latestDisbResp.Models.FirstOrDefault()?.RecordDate ?? DateTime.MinValue;

                    var latestDate = latestGivingDate > latestDisbDate ? latestGivingDate : latestDisbDate;
                    latestMonth = latestDate != DateTime.MinValue ? latestDate.ToString("yyyy-MM") : DateTime.Today.ToString("yyyy-MM");

                    var latestMonthOptions = new MemoryCacheEntryOptions()
                        .SetSize(1)
                        .SetAbsoluteExpiration(TimeSpan.FromHours(1));
                    _cache.Set("LatestReportMonth", latestMonth, latestMonthOptions);
                }
                SelectedMonth = latestMonth;
            }

            // 2. Define our unique Cache Key for this specific month
            string cacheKey = $"SystemReport_{SelectedMonth}";

            // 3. Check the RAM: Does this month already exist in memory?
            if (_cache.TryGetValue(cacheKey, out MonthlyFinancialReportVm cachedReport))
            {
                // CACHE HIT (0.001 seconds)
                MonthReport = cachedReport;

                // We also need to restore the UI totals from the cached object
                MonthBegBalance = cachedReport.Pages.FirstOrDefault()?.Report.OverallBegBalance ?? 0; // We will store this safely below
                MonthTotalReceipts = cachedReport.Pages.Sum(p => p.Report.Giving.GrandTotal);
                MonthTotalDisbursements = cachedReport.Pages.Sum(p => p.Report.Disbursement.TotalNetDisbursement);
                MonthNetBalance = MonthBegBalance + MonthTotalReceipts - MonthTotalDisbursements;

                var firstDayCached = new DateTime(int.Parse(SelectedMonth.Split('-')[0]), int.Parse(SelectedMonth.Split('-')[1]), 1);
                PreviousMonthLabel = firstDayCached.AddMonths(-1).ToString("MMMM yyyy");

                return; // Stop here! Don't talk to Supabase.
            }

            // ==========================================
            // CACHE MISS: Run the heavy database logic
            // ==========================================
            var firstDay = new DateTime(int.Parse(SelectedMonth.Split('-')[0]), int.Parse(SelectedMonth.Split('-')[1]), 1);
            var lastDay = firstDay.AddMonths(1).AddDays(-1);
            string firstDayStr = firstDay.ToString("yyyy-MM-dd");
            string lastDayStr = lastDay.ToString("yyyy-MM-dd");

            MonthReport = new MonthlyFinancialReportVm
            {
                SelectedMonth = SelectedMonth,
                MonthLabel = firstDay.ToString("MMMM yyyy"),
                Pages = new List<MonthlyReportPageVm>()
            };

            var givingResp = await _supabase.Client.From<GivingRecord>()
                .Filter("service_date", Operator.GreaterThanOrEqual, firstDayStr)
                .Filter("service_date", Operator.LessThanOrEqual, lastDayStr).Get();

            var monthGivingRecords = givingResp.Models.ToList();
            var givingIds = monthGivingRecords.Select(g => (object)g.Id).ToList();

            var monthGivingEntries = new List<GivingEntry>();
            var monthDenominations = new List<GivingDenomination>();
            var monthDisbRecords = new List<DisbursementRecord>();

            if (givingIds.Any())
            {
                var entriesTask = _supabase.Client.From<GivingEntry>().Filter("giving_record_id", Operator.In, givingIds).Get();
                var denomTask = _supabase.Client.From<GivingDenomination>().Filter("giving_record_id", Operator.In, givingIds).Get();
                var disbTask = _supabase.Client.From<DisbursementRecord>().Filter("giving_record_id", Operator.In, givingIds).Get();

                await Task.WhenAll(entriesTask, denomTask, disbTask);

                monthGivingEntries = entriesTask.Result.Models.ToList();
                monthDenominations = denomTask.Result.Models.ToList();
                monthDisbRecords = disbTask.Result.Models.ToList();
            }

            var disbIds = monthDisbRecords.Select(d => (object)d.Id).ToList();
            var monthVouchers = new List<Voucher>();

            if (disbIds.Any())
            {
                var vResp = await _supabase.Client.From<Voucher>().Filter("disbursement_record_id", Operator.In, disbIds).Get();
                monthVouchers = vResp.Models.ToList();
            }

            var voucherIds = monthVouchers.Select(v => (object)v.Id).ToList();
            var memberIds = monthGivingEntries.Where(e => e.MemberId.HasValue).Select(e => (object)e.MemberId.Value).Distinct().ToList();

            var allVoucherItems = new List<VoucherItem>();
            var allMembers = new List<Member>();
            var tasks = new List<Task>();

            if (voucherIds.Any())
                tasks.Add(_supabase.Client.From<VoucherItem>().Filter("voucher_id", Operator.In, voucherIds).Get().ContinueWith(t => allVoucherItems = t.Result.Models.ToList()));

            if (memberIds.Any())
                tasks.Add(_supabase.Client.From<Member>().Filter("id", Operator.In, memberIds).Get().ContinueWith(t => allMembers = t.Result.Models.ToList()));

            if (tasks.Any())
                await Task.WhenAll(tasks);

            var sundays = GetAllSundaysInMonth(firstDay, lastDay);
            foreach (var sunday in sundays)
            {
                var page = BuildMonthlyPage(sunday, allMembers, allVoucherItems, monthGivingRecords, monthGivingEntries, monthDenominations, monthDisbRecords, monthVouchers);
                MonthReport.Pages.Add(page);
            }

            var previousMonthStr = firstDay.AddMonths(-1).ToString("yyyy-MM");
            PreviousMonthLabel = firstDay.AddMonths(-1).ToString("MMMM yyyy");

            var prevBalancesResponse = await _supabase.Client.From<MonthlyFundBalance>()
                .Filter("report_month", Operator.Equals, previousMonthStr).Get();

            MonthBegBalance = prevBalancesResponse.Models.Sum(b => b.EndingBalance);
            MonthTotalReceipts = monthGivingRecords.Sum(g => g.GrandTotal);

            var allMonthDisbResp = await _supabase.Client.From<DisbursementRecord>()
                .Filter("record_date", Operator.GreaterThanOrEqual, firstDayStr)
                .Filter("record_date", Operator.LessThanOrEqual, lastDayStr).Get();

            MonthTotalDisbursements = allMonthDisbResp.Models.Sum(d => d.TotalReleased - d.TotalReturned);
            MonthNetBalance = MonthBegBalance + MonthTotalReceipts - MonthTotalDisbursements;

            // To safely store MonthBegBalance in the cache without breaking models, we hack it onto the first page object temporarily.
            if (MonthReport.Pages.Any())
            {
                MonthReport.Pages[0].Report.OverallBegBalance = MonthBegBalance;
            }

            // 4. Save to RAM for the next request
            var cacheEntryOptions = new MemoryCacheEntryOptions()
                .SetSize(10)
                .SetSlidingExpiration(TimeSpan.FromHours(12));

            _cache.Set(cacheKey, MonthReport, cacheEntryOptions);
        }

        private MonthlyReportPageVm BuildMonthlyPage(DateTime reportDate, List<Member> members, List<VoucherItem> allVoucherItems,
            List<GivingRecord> monthGivingRecords, List<GivingEntry> monthGivingEntries, List<GivingDenomination> monthDenominations,
            List<DisbursementRecord> monthDisbRecords, List<Voucher> monthVouchers)
        {
            var safeDate = reportDate.Date.AddHours(12);
            string dateOnly = safeDate.ToString("yyyy-MM-dd");

            var page = new MonthlyReportPageVm
            {
                ReportDate = safeDate,
                HasReport = false,
                EmptyMessage = "No report yet for this Sunday.",
                Report = new WeeklyFinancialReportVm { ReportDate = safeDate }
            };

            // Lookup from memory list
            var givingRecord = monthGivingRecords.FirstOrDefault(g => g.ServiceDate.ToString("yyyy-MM-dd") == dateOnly);

            if (givingRecord == null) return page;

            page.HasReport = true;
            page.EmptyMessage = "";

            page.Report.Giving.RecordCode = givingRecord.RecordCode;
            page.Report.Giving.TotalTithes = givingRecord.TotalTithes;
            page.Report.Giving.TotalOfferings = givingRecord.TotalOfferings;
            page.Report.Giving.TotalSolomon = givingRecord.TotalSolomon;
            page.Report.Giving.TotalNoah = givingRecord.TotalNoah;
            page.Report.Giving.TotalMission = givingRecord.TotalMission;
            page.Report.Giving.TotalOthers = givingRecord.TotalOthers;
            page.Report.Giving.GrandTotal = givingRecord.GrandTotal;

            // Lookup from memory list
            var givingEntries = monthGivingEntries.Where(e => e.GivingRecordId == givingRecord.Id).ToList();

            page.Report.Giving.Rows = givingEntries
                .Select(e =>
                {
                    string name = e.MemberId.HasValue
                        ? members.FirstOrDefault(m => m.Id == e.MemberId.Value)?.Name ?? "(Unknown Member)"
                        : e.EntryName ?? "(Unnamed)";

                    string lower = name.ToLower();
                    bool isAnonymous = lower.Contains("anonymous");
                    bool isGroup = lower.Contains("kids") || lower.Contains("prayer") || lower.Contains("youth") || lower.Contains("camp") || lower.Contains("meeting") || lower.Contains("group");
                    bool isFamily = name.Contains("&");
                    bool isOthersOnly = e.Tithes == 0 && e.Offerings == 0 && e.Solomon == 0 && e.Noah == 0 && e.Mission == 0 && e.Others > 0;

                    int sortGroup = 0;
                    if (isOthersOnly) sortGroup = 5;
                    else if (isAnonymous) sortGroup = 4;
                    else if (isGroup) sortGroup = 3;
                    else if (isFamily) sortGroup = 2;
                    else sortGroup = 1;

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
                .OrderBy(x => x.SortGroup).ThenBy(x => x.Name).ToList();

            // Lookup from memory list
            var denomination = monthDenominations.FirstOrDefault(d => d.GivingRecordId == givingRecord.Id);

            if (denomination != null)
            {
                page.Report.Denomination.Exists = true;
                page.Report.Denomination.Lines = BuildDenominationLines(denomination);
                page.Report.Denomination.Total = denomination.Total;
            }

            // Lookup from memory list
            var disbursementRecord = monthDisbRecords.FirstOrDefault(d => d.GivingRecordId == givingRecord.Id);

            if (disbursementRecord != null)
            {
                page.Report.Disbursement.TotalReleased = disbursementRecord.TotalReleased;
                page.Report.Disbursement.TotalReturned = disbursementRecord.TotalReturned;
                page.Report.Disbursement.TotalNetDisbursement = disbursementRecord.TotalReleased - disbursementRecord.TotalReturned;

                // Lookup from memory list
                var vouchers = monthVouchers.Where(v => v.DisbursementRecordId == disbursementRecord.Id).ToList();

                page.Report.Disbursement.Vouchers = vouchers
                    .OrderBy(v => { int parsed; return int.TryParse(v.VoucherNumber, out parsed) ? parsed : int.MaxValue; })
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
                            }).ToList();

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
                    }).ToList();

                page.Report.Disbursement.Groups = page.Report.Disbursement.Vouchers
                    .GroupBy(v => v.Ministry)
                    .Select(group =>
                    {
                        var orderedVouchers = group.OrderBy(v => { int parsed; return int.TryParse(v.VoucherNumber, out parsed) ? parsed : int.MaxValue; }).ThenBy(v => v.VoucherNumber).ToList();
                        var lines = orderedVouchers.SelectMany(v => v.Lines).ToList();
                        var firstVoucherNumber = orderedVouchers.FirstOrDefault()?.VoucherNumber ?? "";
                        return new DisbursementGroupVm { Ministry = group.Key, Lines = lines, GroupTotal = lines.Sum(x => x.NetAmount), SortVoucherNumber = firstVoucherNumber };
                    })
                    .OrderBy(x => { int parsed; return int.TryParse(x.SortVoucherNumber, out parsed) ? parsed : int.MaxValue; })
                    .ThenBy(x => x.SortVoucherNumber).ToList();
            }

            page.Report.Summary.CashReceiptsOrBlessings = page.Report.Giving.GrandTotal;
            page.Report.Summary.LessCashDisbursements = page.Report.Disbursement.TotalNetDisbursement;
            page.Report.Summary.NetCashBalance = page.Report.Summary.CashReceiptsOrBlessings - page.Report.Summary.LessCashDisbursements;

            return page;
        }

        private List<DateTime> GetAllSundaysInMonth(DateTime firstDay, DateTime lastDay)
        {
            var sundays = new List<DateTime>();
            for (var date = firstDay.Date; date <= lastDay.Date; date = date.AddDays(1))
            {
                if (date.DayOfWeek == DayOfWeek.Sunday)
                {
                    sundays.Add(date);
                }
            }
            return sundays;
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
            if (qty <= 0) return;
            lines.Add(new DenominationLineVm { Label = label, Quantity = qty, UnitValue = unitValue, LineTotal = qty * unitValue });
        }

        private string BuildSummaryLabel(string particular, string payee)
        {
            var cleanParticular = (particular ?? "").Trim();
            var cleanPayee = (payee ?? "").Trim();
            var lower = cleanParticular.ToLower();

            bool includePayee = lower.Contains("stipend") || lower.Contains("professional fee");

            if (includePayee && !string.IsNullOrWhiteSpace(cleanPayee))
            {
                return $"{cleanParticular} - {cleanPayee}";
            }

            return cleanParticular;
        }

        private (string DenomValue, string DenomType) SplitDenominationLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return ("", "");
            var parts = label.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1) return (parts[0], "");
            return (parts[0], string.Join(" ", parts.Skip(1)));
        }

        private void SetCell(List<List<object>> grid, int row, int col, object value)
        {
            while (grid.Count <= row) grid.Add(new List<object>());
            while (grid[row].Count <= col) grid[row].Add("");
            grid[row][col] = value ?? "";
        }

        // ====================================================================
        // BUTTON 1: EXPORT DETAILED MONTHLY RECORDS
        // ====================================================================
        public async Task<IActionResult> OnGetExportAsync() => await ExportRecordsAsync();
        public async Task<IActionResult> OnPostExportAsync() => await ExportRecordsAsync();

        private async Task<IActionResult> ExportRecordsAsync()
        {
            await LoadMonthAsync();

            var pagesWithRecords = MonthReport.Pages
                .Where(x => x.HasReport)
                .OrderBy(x => x.ReportDate)
                .ToList();

            if (!pagesWithRecords.Any())
            {
                Message = "No records found for this month.";
                return Page();
            }

            string detailedFingerprint = $"DetailedExport_{SelectedMonth}_{MonthTotalReceipts}_{MonthTotalDisbursements}";
            var logQuery = await _supabase.Client.From<ExportLogRecord>().Filter("report_month", Operator.Equals, SelectedMonth).Get();
            var existingLog = logQuery.Models.FirstOrDefault();

            if (existingLog != null && existingLog.DetailedFingerprint == detailedFingerprint)
            {
                Message = "Your Detailed Report is already up to date in Google Sheets! No new changes detected.";
                return Page();
            }

            string credentialPath = "google-credentials.json"; 
            string spreadsheetId = AppSettings.DetailedSheetId;

            GoogleCredential credential;
            using (var stream = new FileStream(credentialPath, FileMode.Open, FileAccess.Read))
            {
#pragma warning disable CS0618 
                credential = GoogleCredential.FromStream(stream).CreateScoped(SheetsService.Scope.Spreadsheets);
#pragma warning restore CS0618 
            }

            var service = new SheetsService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = "FinanceApp"
            });

            var tabName = MonthReport.MonthLabel.ToUpper();
            var spreadsheet = await service.Spreadsheets.Get(spreadsheetId).ExecuteAsync();
            var existingSheet = spreadsheet.Sheets.FirstOrDefault(s => s.Properties.Title == tabName);

            // 1. Ensure we have the date for comparison
            if (!DateTime.TryParse($"{SelectedMonth}-01", out var monthStart))
            {
                monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            }

            // 2. Calculate chronological index based on existing tab names
            int targetIndex = 0;
            foreach (var s in spreadsheet.Sheets)
            {
                if (s.Properties.Title == tabName) continue;

                if (DateTime.TryParseExact(s.Properties.Title, "MMMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var sheetDate))
                {
                    if (monthStart < sheetDate)
                    {
                        break;
                    }
                }
                targetIndex++;
            }

            var batchRequests = new List<Request>();

            // 3. Queue delete if it exists
            if (existingSheet != null)
            {
                int oldSheetId = existingSheet.Properties.SheetId ?? 0;
                batchRequests.Add(new Request
                {
                    DeleteSheet = new DeleteSheetRequest { SheetId = oldSheetId }
                });
            }

            // 4. Queue add with the calculated chronological Index
            batchRequests.Add(new Request
            {
                AddSheet = new AddSheetRequest
                {
                    Properties = new SheetProperties
                    {
                        Title = tabName,
                        Index = targetIndex,
                        GridProperties = new GridProperties 
                        {
                            HideGridlines = true
                        }
                    }
                }
            });

            // 5. Execute in one fast batch
            var response = await service.Spreadsheets.BatchUpdate(
                new BatchUpdateSpreadsheetRequest { Requests = batchRequests },
                spreadsheetId).ExecuteAsync();

            var addSheetReply = response.Replies.FirstOrDefault(r => r.AddSheet != null)?.AddSheet;
            int sheetId = addSheetReply?.Properties.SheetId ?? 0;

            List<List<object>> grid = new List<List<object>>();
            List<Request> formatRequests = new List<Request>();

            int currentRow = 0;

            foreach (var page in pagesWithRecords)
            {
                int maxRowUsed = BuildGoogleSheetData(grid, formatRequests, sheetId, currentRow, page.Report);
                currentRow = maxRowUsed + 3;
            }

            // 1. APPLY ARIAL 11 STRICTLY TO AVOID 500 ERRORS
            formatRequests.Add(new Request
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = new GridRange { SheetId = sheetId, StartRowIndex = 0, EndRowIndex = currentRow > 0 ? currentRow : 1000, StartColumnIndex = 0, EndColumnIndex = 25 },
                    Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { FontFamily = "Arial", FontSize = 11 } } },
                    Fields = "userEnteredFormat.textFormat.fontFamily,userEnteredFormat.textFormat.fontSize"
                }
            });

            // 2. WRITE DATA
            IList<IList<object>> allRows = grid.Select(r => (IList<object>)r).ToList();
            var valueRange = new ValueRange { Values = allRows };
            var updateRequest = service.Spreadsheets.Values.Update(valueRange, spreadsheetId, $"{tabName}!A1");
            updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
            await updateRequest.ExecuteAsync();

            // ── COLUMN RESIZING FIXES ──
            var resizeManualA = new Request { UpdateDimensionProperties = new UpdateDimensionPropertiesRequest { Range = new DimensionRange { SheetId = sheetId, Dimension = "COLUMNS", StartIndex = 0, EndIndex = 1 }, Properties = new DimensionProperties { PixelSize = 280 }, Fields = "pixelSize" } };
            var resizeAuto1 = new Request { AutoResizeDimensions = new AutoResizeDimensionsRequest { Dimensions = new DimensionRange { SheetId = sheetId, Dimension = "COLUMNS", StartIndex = 1, EndIndex = 12 } } };
            var resizeManualM = new Request { UpdateDimensionProperties = new UpdateDimensionPropertiesRequest { Range = new DimensionRange { SheetId = sheetId, Dimension = "COLUMNS", StartIndex = 12, EndIndex = 13 }, Properties = new DimensionProperties { PixelSize = 50 }, Fields = "pixelSize" } };

            var resizeAutoN = new Request { AutoResizeDimensions = new AutoResizeDimensionsRequest { Dimensions = new DimensionRange { SheetId = sheetId, Dimension = "COLUMNS", StartIndex = 13, EndIndex = 14 } } };
            var resizeManualO = new Request { UpdateDimensionProperties = new UpdateDimensionPropertiesRequest { Range = new DimensionRange { SheetId = sheetId, Dimension = "COLUMNS", StartIndex = 14, EndIndex = 15 }, Properties = new DimensionProperties { PixelSize = 80 }, Fields = "pixelSize" } }; // COLUMN O
            var resizeManualP = new Request { UpdateDimensionProperties = new UpdateDimensionPropertiesRequest { Range = new DimensionRange { SheetId = sheetId, Dimension = "COLUMNS", StartIndex = 15, EndIndex = 16 }, Properties = new DimensionProperties { PixelSize = 80 }, Fields = "pixelSize" } }; // COLUMN P

            var resizeManualQ = new Request { UpdateDimensionProperties = new UpdateDimensionPropertiesRequest { Range = new DimensionRange { SheetId = sheetId, Dimension = "COLUMNS", StartIndex = 16, EndIndex = 17 }, Properties = new DimensionProperties { PixelSize = 95 }, Fields = "pixelSize" } };
            var resizeManualR = new Request { UpdateDimensionProperties = new UpdateDimensionPropertiesRequest { Range = new DimensionRange { SheetId = sheetId, Dimension = "COLUMNS", StartIndex = 17, EndIndex = 18 }, Properties = new DimensionProperties { PixelSize = 95 }, Fields = "pixelSize" } };

            var resizeAutoS = new Request { AutoResizeDimensions = new AutoResizeDimensionsRequest { Dimensions = new DimensionRange { SheetId = sheetId, Dimension = "COLUMNS", StartIndex = 18, EndIndex = 19 } } };
            var resizeManualT = new Request { UpdateDimensionProperties = new UpdateDimensionPropertiesRequest { Range = new DimensionRange { SheetId = sheetId, Dimension = "COLUMNS", StartIndex = 19, EndIndex = 20 }, Properties = new DimensionProperties { PixelSize = 50 }, Fields = "pixelSize" } }; // COLUMN T
            var resizeAutoU = new Request { AutoResizeDimensions = new AutoResizeDimensionsRequest { Dimensions = new DimensionRange { SheetId = sheetId, Dimension = "COLUMNS", StartIndex = 20, EndIndex = 21 } } };

            var resizeManualV = new Request { UpdateDimensionProperties = new UpdateDimensionPropertiesRequest { Range = new DimensionRange { SheetId = sheetId, Dimension = "COLUMNS", StartIndex = 21, EndIndex = 22 }, Properties = new DimensionProperties { PixelSize = 80 }, Fields = "pixelSize" } }; // COLUMN V
            var resizeManualW = new Request { UpdateDimensionProperties = new UpdateDimensionPropertiesRequest { Range = new DimensionRange { SheetId = sheetId, Dimension = "COLUMNS", StartIndex = 22, EndIndex = 23 }, Properties = new DimensionProperties { PixelSize = 80 }, Fields = "pixelSize" } }; // COLUMN W

            var resizeRequests = new List<Request>
            {
                resizeManualA, resizeAuto1, resizeManualM,
                resizeAutoN, resizeManualO, resizeManualP,
                resizeManualQ, resizeManualR,
                resizeAutoS, resizeManualT, resizeAutoU, resizeManualV, resizeManualW
            };

            await service.Spreadsheets.BatchUpdate(new BatchUpdateSpreadsheetRequest { Requests = resizeRequests }, spreadsheetId).ExecuteAsync();
            // 4. APPLY FORMATTING EXCLUSIVE OF NATIVE TABLES (Prevents 500 Crash)
            var stylingRequests = formatRequests.Where(r => r.AddTable == null).ToList();
            if (stylingRequests.Any())
            {
                await service.Spreadsheets.BatchUpdate(new BatchUpdateSpreadsheetRequest { Requests = stylingRequests }, spreadsheetId).ExecuteAsync();
            }

            // 5. SAFELY APPLY TABLES IN SEPARATE BATCH
            var tableRequests = formatRequests.Where(r => r.AddTable != null).ToList();
            if (tableRequests.Any())
            {
                try
                {
                    await service.Spreadsheets.BatchUpdate(new BatchUpdateSpreadsheetRequest { Requests = tableRequests }, spreadsheetId).ExecuteAsync();
                }
                catch (Exception ex)
                {
                    // Catch the 500 Error and return a step-by-step guide to the user
                    if (ex.Message.Contains("already exists") || ex.Message.Contains("Internal error") || ex.Message.Contains("500"))
                    {
                        Message = "FAILED TO EXPORT: Duplicate Record Code detected! " +
                                  "Google Sheets requires every Record Code to be unique across the ENTIRE document. " +
                                  "HOW TO FIX: " +
                                  "1. Check if you accidentally reused a Record Code (like CR_01) from a previous month. " +
                                  "2. Go to the Giving module and edit this month's Record Code to make it unique (e.g., CR_FEB_01). " +
                                  "3. Try exporting again.";

                        await LoadMonthAsync();
                        return Page();
                    }

                    // Fallback for other random API errors
                    Message = $"FAILED TO EXPORT: Google API Error - {ex.Message}";
                    await LoadMonthAsync();
                    return Page();
                }
            }

            var detailExportOptions = new MemoryCacheEntryOptions()
                .SetSize(1) 
                .SetAbsoluteExpiration(TimeSpan.FromDays(7));
            _cache.Set(detailedFingerprint, true, detailExportOptions);

            var logToSave = existingLog ?? new ExportLogRecord { ReportMonth = SelectedMonth };
            logToSave.DetailedFingerprint = detailedFingerprint;
            await _supabase.Client.From<ExportLogRecord>().Upsert(logToSave);

            await LoadMonthAsync();
            Message = $"Successfully exported Detailed Records: {tabName}!";
            return Page();
        }

        private int BuildGoogleSheetData(List<List<object>> grid, List<Request> formatRequests, int sheetId, int startRow, WeeklyFinancialReportVm report)
        {
            int dataRow = startRow + 2;

            int colGiving = 1;
            int colDisb = 12;
            int colSum = 19;

            int givingEndRow = WriteGivingToGrid(grid, formatRequests, sheetId, dataRow, colGiving, report);
            int disbEndRow = WriteDisbursementToGrid(grid, formatRequests, sheetId, dataRow, colDisb, report);
            int sumEndRow = WriteSummaryToGrid(grid, formatRequests, sheetId, dataRow, colSum, report);

            return Math.Max(givingEndRow, Math.Max(disbEndRow, sumEndRow));
        }

        private int WriteGivingToGrid(List<List<object>> grid, List<Request> formatRequests, int sheetId, int row, int col, WeeklyFinancialReportVm report)
        {
            // STRICT RECORD CODE NAME
            string rawName = report.Giving.RecordCode ?? "CR_00";
            string safeTableName = Regex.Replace(rawName, "[^a-zA-Z0-9_]", "");
            if (safeTableName.Length > 0 && char.IsDigit(safeTableName[0])) safeTableName = "T_" + safeTableName;

            int givingStartRow = row;

            SetCell(grid, row, col, $"SOURCES OF BLESSINGS {report.ReportDate:MM/dd/yyyy}   ");
            SetCell(grid, row, col + 1, "TITHES   ");
            SetCell(grid, row, col + 2, "OFFERINGS   ");
            SetCell(grid, row, col + 3, "SOLOMON   ");
            SetCell(grid, row, col + 4, "NOAH   ");
            SetCell(grid, row, col + 5, "MISSION   ");
            SetCell(grid, row, col + 6, "OTHERS   ");
            SetCell(grid, row, col + 7, "TOTAL   ");
            row++;

            foreach (var item in report.Giving.Rows)
            {
                SetCell(grid, row, col, item.Name);
                SetCell(grid, row, col + 1, item.Tithes > 0 ? item.Tithes : "");
                SetCell(grid, row, col + 2, item.Offerings > 0 ? item.Offerings : "");
                SetCell(grid, row, col + 3, item.Solomon > 0 ? item.Solomon : "");
                SetCell(grid, row, col + 4, item.Noah > 0 ? item.Noah : "");
                SetCell(grid, row, col + 5, item.Mission > 0 ? item.Mission : "");
                SetCell(grid, row, col + 6, item.Others > 0 ? item.Others : "");

                decimal rowTotal = item.Tithes + item.Offerings + item.Solomon + item.Noah + item.Mission + item.Others;
                SetCell(grid, row, col + 7, rowTotal > 0 ? rowTotal : "");

                row++;
            }

            int lastItemRow = row - 1;
            if (givingStartRow < lastItemRow)
            {
                formatRequests.Add(new Request
                {
                    UpdateBorders = new UpdateBordersRequest
                    {
                        Range = new GridRange { SheetId = sheetId, StartRowIndex = lastItemRow, EndRowIndex = lastItemRow + 1, StartColumnIndex = col, EndColumnIndex = col + 8 },
                        Bottom = new Border { Style = "SOLID", Color = new Color { Red = 0f, Green = 0f, Blue = 0f } }
                    }
                });
            }

            SetCell(grid, row, col, "TOTAL GIVING");
            SetCell(grid, row, col + 1, report.Giving.TotalTithes);
            SetCell(grid, row, col + 2, report.Giving.TotalOfferings);
            SetCell(grid, row, col + 3, report.Giving.TotalSolomon);
            SetCell(grid, row, col + 4, report.Giving.TotalNoah);
            SetCell(grid, row, col + 5, report.Giving.TotalMission);
            SetCell(grid, row, col + 6, report.Giving.TotalOthers);
            SetCell(grid, row, col + 7, report.Giving.GrandTotal);
            row++;

            formatRequests.Add(new Request
            {
                AddTable = new AddTableRequest
                {
                    Table = new Table
                    {
                        Name = safeTableName,
                        Range = new GridRange { SheetId = sheetId, StartRowIndex = givingStartRow, EndRowIndex = row, StartColumnIndex = col, EndColumnIndex = col + 8 }
                    }
                }
            });

            FormatTableTotalsRow(formatRequests, sheetId, row - 1, col, col + 8);
            AddNumberFormatting(formatRequests, sheetId, givingStartRow + 1, row, col + 1, col + 8);

            row += 2;

            int denomStartRow = row;

            SetCell(grid, row, col, "DENOMINATION   ");
            SetCell(grid, row, col + 1, "TYPE   ");
            SetCell(grid, row, col + 2, "QTY   ");
            SetCell(grid, row, col + 3, "UNIT   ");
            SetCell(grid, row, col + 4, "TOTAL   ");
            row++;

            foreach (var d in report.Denomination.Lines)
            {
                var split = SplitDenominationLabel(d.Label);

                if (decimal.TryParse(split.DenomValue.Replace(",", ""), out var denomNumeric))
                {
                    SetCell(grid, row, col, denomNumeric);
                }
                else
                {
                    SetCell(grid, row, col, split.DenomValue);
                }

                SetCell(grid, row, col + 1, split.DenomType);
                SetCell(grid, row, col + 2, d.Quantity);
                SetCell(grid, row, col + 3, d.UnitValue);
                SetCell(grid, row, col + 4, d.LineTotal);
                row++;
            }

            SetCell(grid, row, col + 3, "TOTAL");
            SetCell(grid, row, col + 4, report.Denomination.Total);
            row++;

            formatRequests.Add(new Request
            {
                AddTable = new AddTableRequest
                {
                    Table = new Table
                    {
                        Name = "DENOM_" + safeTableName,
                        Range = new GridRange { SheetId = sheetId, StartRowIndex = denomStartRow, EndRowIndex = row, StartColumnIndex = col, EndColumnIndex = col + 5 }
                    }
                }
            });

            FormatTableTotalsRow(formatRequests, sheetId, row - 1, col, col + 5);
            AddNumberFormatting(formatRequests, sheetId, denomStartRow + 1, row, col, col + 1, "#,##0");
            AddNumberFormatting(formatRequests, sheetId, denomStartRow + 1, row, col + 3, col + 5);

            if (denomStartRow + 1 < row - 1)
            {
                formatRequests.Add(new Request
                {
                    RepeatCell = new RepeatCellRequest
                    {
                        Range = new GridRange { SheetId = sheetId, StartRowIndex = denomStartRow + 1, EndRowIndex = row - 1, StartColumnIndex = col, EndColumnIndex = col + 1 },
                        Cell = new CellData { UserEnteredFormat = new CellFormat { HorizontalAlignment = "RIGHT" } },
                        Fields = "userEnteredFormat.horizontalAlignment"
                    }
                });
            }

            return row;
        }

        private int WriteDisbursementToGrid(List<List<object>> grid, List<Request> formatRequests, int sheetId, int row, int col, WeeklyFinancialReportVm report)
        {
            int disbStartRow = row;

            SetCell(grid, row, col, "PARTICULARS");
            SetCell(grid, row, col + 2, "AMOUNT");
            SetCell(grid, row, col + 3, "TOTAL");
            SetCell(grid, row, col + 4, "VOUCHER #");
            SetCell(grid, row, col + 5, "cash return");
            row++;

            foreach (var voucher in report.Disbursement.Vouchers)
            {
                SetCell(grid, row, col, $"{voucher.Ministry} - {voucher.Payee}");
                row++;

                var lines = voucher.Lines ?? new List<DisbursementLineVm>();
                bool isSingle = lines.Count == 1;

                for (int i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    bool isLast = i == lines.Count - 1;

                    SetCell(grid, row, col + 1, line.Particular);

                    if (isSingle)
                    {
                        SetCell(grid, row, col + 3, line.AmountReleased);
                    }
                    else
                    {
                        SetCell(grid, row, col + 2, line.AmountReleased);
                        if (isLast)
                        {
                            SetCell(grid, row, col + 3, voucher.VoucherTotalReleased);

                            formatRequests.Add(new Request
                            {
                                UpdateBorders = new UpdateBordersRequest
                                {
                                    Range = new GridRange { SheetId = sheetId, StartRowIndex = row, EndRowIndex = row + 1, StartColumnIndex = col + 2, EndColumnIndex = col + 3 },
                                    Bottom = new Border { Style = "SOLID", Color = new Color { Red = 0f, Green = 0f, Blue = 0f } }
                                }
                            });
                        }
                    }

                    if (isLast)
                    {
                        string vStr = voucher.VoucherNumber ?? "";
                        if (int.TryParse(vStr, out _))
                        {
                            SetCell(grid, row, col + 4, "'" + vStr.PadLeft(3, '0'));
                        }
                        else
                        {
                            SetCell(grid, row, col + 4, "'" + vStr);
                        }
                    }

                    if (line.CashReturned > 0) SetCell(grid, row, col + 5, line.CashReturned);

                    row++;
                }
            }

            int totalDisbRow = row;
            SetCell(grid, row, col, "TOTAL");
            SetCell(grid, row, col + 3, report.Disbursement.TotalReleased);
            if (report.Disbursement.TotalReturned > 0) SetCell(grid, row, col + 5, report.Disbursement.TotalReturned);
            row++;

            if (disbStartRow < totalDisbRow)
            {
                formatRequests.Add(new Request
                {
                    RepeatCell = new RepeatCellRequest
                    {
                        Range = new GridRange { SheetId = sheetId, StartRowIndex = disbStartRow, EndRowIndex = disbStartRow + 1, StartColumnIndex = col, EndColumnIndex = col + 6 },
                        Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } },
                        Fields = "userEnteredFormat.textFormat.bold"
                    }
                });

                formatRequests.Add(new Request
                {
                    RepeatCell = new RepeatCellRequest
                    {
                        Range = new GridRange { SheetId = sheetId, StartRowIndex = disbStartRow, EndRowIndex = disbStartRow + 1, StartColumnIndex = col + 5, EndColumnIndex = col + 6 },
                        Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { ForegroundColor = new Color { Red = 1f, Green = 0f, Blue = 0f } } } },
                        Fields = "userEnteredFormat.textFormat.foregroundColor"
                    }
                });

                formatRequests.Add(new Request
                {
                    RepeatCell = new RepeatCellRequest
                    {
                        Range = new GridRange { SheetId = sheetId, StartRowIndex = disbStartRow, EndRowIndex = disbStartRow + 1, StartColumnIndex = col + 2, EndColumnIndex = col + 6 },
                        Cell = new CellData { UserEnteredFormat = new CellFormat { HorizontalAlignment = "RIGHT" } },
                        Fields = "userEnteredFormat.horizontalAlignment"
                    }
                });
            }

            if (disbStartRow + 1 < row)
            {
                formatRequests.Add(new Request
                {
                    RepeatCell = new RepeatCellRequest
                    {
                        Range = new GridRange { SheetId = sheetId, StartRowIndex = disbStartRow + 1, EndRowIndex = row, StartColumnIndex = col + 4, EndColumnIndex = col + 5 },
                        Cell = new CellData { UserEnteredFormat = new CellFormat { HorizontalAlignment = "RIGHT" } },
                        Fields = "userEnteredFormat.horizontalAlignment"
                    }
                });
            }

            formatRequests.Add(new Request
            {
                UpdateBorders = new UpdateBordersRequest
                {
                    Range = new GridRange { SheetId = sheetId, StartRowIndex = totalDisbRow, EndRowIndex = totalDisbRow + 1, StartColumnIndex = col + 3, EndColumnIndex = col + 4 },
                    Top = new Border { Style = "SOLID", Color = new Color { Red = 0f, Green = 0f, Blue = 0f } }
                }
            });

            if (report.Disbursement.TotalReturned > 0)
            {
                formatRequests.Add(new Request
                {
                    UpdateBorders = new UpdateBordersRequest
                    {
                        Range = new GridRange { SheetId = sheetId, StartRowIndex = totalDisbRow, EndRowIndex = totalDisbRow + 1, StartColumnIndex = col + 5, EndColumnIndex = col + 6 },
                        Top = new Border { Style = "SOLID", Color = new Color { Red = 0f, Green = 0f, Blue = 0f } }
                    }
                });
            }

            AddNumberFormatting(formatRequests, sheetId, disbStartRow + 1, row, col + 2, col + 4);
            AddNumberFormatting(formatRequests, sheetId, disbStartRow + 1, row, col + 5, col + 6);

            if (disbStartRow + 1 < row)
            {
                formatRequests.Add(new Request
                {
                    RepeatCell = new RepeatCellRequest
                    {
                        Range = new GridRange { SheetId = sheetId, StartRowIndex = disbStartRow + 1, EndRowIndex = row, StartColumnIndex = col + 5, EndColumnIndex = col + 6 },
                        Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { ForegroundColor = new Color { Red = 1f, Green = 0f, Blue = 0f } } } },
                        Fields = "userEnteredFormat.textFormat.foregroundColor"
                    }
                });
            }

            // --- RESTORED NET CASH SUMMARY LOGIC ---
            row += 1;
            int summaryBlockStart = row;

            SetCell(grid, row, col, "Adjusted disbursement");
            SetCell(grid, row, col + 3, report.Disbursement.TotalNetDisbursement);
            formatRequests.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = row, EndRowIndex = row + 1, StartColumnIndex = col, EndColumnIndex = col + 4 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } }, Fields = "userEnteredFormat.textFormat.bold" } });
            row += 2;

            SetCell(grid, row, col, "Cash Receipts / Blessings");
            SetCell(grid, row, col + 3, report.Summary.CashReceiptsOrBlessings);
            row++;

            SetCell(grid, row, col, "Less: Cash disbursements");
            SetCell(grid, row, col + 3, report.Summary.LessCashDisbursements);

            // FIX: Targeted 'row' instead of 'row - 1' for the bottom border
            formatRequests.Add(new Request { UpdateBorders = new UpdateBordersRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = row, EndRowIndex = row + 1, StartColumnIndex = col + 3, EndColumnIndex = col + 4 }, Bottom = new Border { Style = "SOLID", Color = new Color { Red = 0f, Green = 0f, Blue = 0f } } } });
            row++;

            SetCell(grid, row, col, $"Net Cash Balance {report.ReportDate:MM/dd/yyyy}");
            SetCell(grid, row, col + 3, report.Summary.NetCashBalance);

            var netBgColor = report.Summary.NetCashBalance < 0 ? new Color { Red = 0.98f, Green = 0.8f, Blue = 0.8f } : new Color { Red = 0.88f, Green = 0.93f, Blue = 0.85f };

            // FIX: Targeted 'row' instead of 'row - 1' for the background color and bold text
            formatRequests.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = row, EndRowIndex = row + 1, StartColumnIndex = col, EndColumnIndex = col + 4 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true }, BackgroundColor = netBgColor } }, Fields = "userEnteredFormat.textFormat.bold,userEnteredFormat.backgroundColor" } });

            // FIX: Expanded EndRowIndex to 'row + 1' to ensure the final Net Cash Balance gets formatted as currency
            AddNumberFormatting(formatRequests, sheetId, summaryBlockStart, row + 1, col + 3, col + 4, "#,##0.00;(#,##0.00)");

            row++;
            return row;
        }

        private int WriteSummaryToGrid(List<List<object>> grid, List<Request> formatRequests, int sheetId, int row, int col, WeeklyFinancialReportVm report)
        {
            int sumStartRow = row;

            SetCell(grid, row, col, "Summary");
            SetCell(grid, row, col + 2, "AMOUNT");
            SetCell(grid, row, col + 3, "TOTAL");
            row++;

            foreach (var group in report.Disbursement.Groups)
            {
                SetCell(grid, row, col, $"{group.Ministry} Expenses");
                row++;

                var lines = group.Lines ?? new List<DisbursementLineVm>();
                bool isSingle = lines.Count == 1;

                for (int i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    bool isLast = i == lines.Count - 1;

                    SetCell(grid, row, col + 1, line.SummaryLabel);

                    if (isSingle)
                    {
                        SetCell(grid, row, col + 3, line.NetAmount);
                    }
                    else
                    {
                        SetCell(grid, row, col + 2, line.NetAmount);
                        if (isLast)
                        {
                            SetCell(grid, row, col + 3, group.GroupTotal);

                            formatRequests.Add(new Request
                            {
                                UpdateBorders = new UpdateBordersRequest
                                {
                                    Range = new GridRange { SheetId = sheetId, StartRowIndex = row, EndRowIndex = row + 1, StartColumnIndex = col + 2, EndColumnIndex = col + 3 },
                                    Bottom = new Border { Style = "SOLID", Color = new Color { Red = 0f, Green = 0f, Blue = 0f } }
                                }
                            });
                        }
                    }
                    row++;
                }
            }

            int totalRow = row;
            SetCell(grid, row, col, "TOTAL");
            SetCell(grid, row, col + 3, report.Disbursement.TotalNetDisbursement);
            row++;

            formatRequests.Add(new Request
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = new GridRange { SheetId = sheetId, StartRowIndex = sumStartRow, EndRowIndex = sumStartRow + 1, StartColumnIndex = col, EndColumnIndex = col + 4 },
                    Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } },
                    Fields = "userEnteredFormat.textFormat.bold"
                }
            });

            formatRequests.Add(new Request
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = new GridRange { SheetId = sheetId, StartRowIndex = totalRow, EndRowIndex = totalRow + 1, StartColumnIndex = col, EndColumnIndex = col + 4 },
                    Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } },
                    Fields = "userEnteredFormat.textFormat.bold"
                }
            });

            formatRequests.Add(new Request
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = new GridRange { SheetId = sheetId, StartRowIndex = sumStartRow, EndRowIndex = sumStartRow + 1, StartColumnIndex = col + 2, EndColumnIndex = col + 4 },
                    Cell = new CellData { UserEnteredFormat = new CellFormat { HorizontalAlignment = "RIGHT" } },
                    Fields = "userEnteredFormat.horizontalAlignment"
                }
            });

            formatRequests.Add(new Request
            {
                UpdateBorders = new UpdateBordersRequest
                {
                    Range = new GridRange { SheetId = sheetId, StartRowIndex = totalRow, EndRowIndex = totalRow + 1, StartColumnIndex = col + 3, EndColumnIndex = col + 4 },
                    Top = new Border { Style = "SOLID", Color = new Color { Red = 0f, Green = 0f, Blue = 0f } }
                }
            });

            AddNumberFormatting(formatRequests, sheetId, sumStartRow + 1, row, col + 2, col + 4);

            return row;
        }

        private void FormatTableTotalsRow(List<Request> requests, int sheetId, int totalsRowIndex, int startCol, int endCol)
        {
            requests.Add(new Request
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = new GridRange { SheetId = sheetId, StartRowIndex = totalsRowIndex, EndRowIndex = totalsRowIndex + 1, StartColumnIndex = startCol, EndColumnIndex = endCol },
                    Cell = new CellData
                    {
                        UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } }
                    },
                    Fields = "userEnteredFormat.textFormat.bold"
                }
            });
        }

        private void AddNumberFormatting(List<Request> requests, int sheetId, int startRow, int endRow, int startCol, int endCol, string pattern = "#,##0.00")
        {
            if (startRow >= endRow) return;

            requests.Add(new Request
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = new GridRange { SheetId = sheetId, StartRowIndex = startRow, EndRowIndex = endRow, StartColumnIndex = startCol, EndColumnIndex = endCol },
                    Cell = new CellData
                    {
                        UserEnteredFormat = new CellFormat
                        {
                            NumberFormat = new NumberFormat { Type = "NUMBER", Pattern = pattern }
                        }
                    },
                    Fields = "userEnteredFormat.numberFormat"
                }
            });
        }



        // ====================================================================
        // BUTTON 2: EXPORT MONTHLY FINANCIAL
        // ====================================================================

        public async Task<IActionResult> OnGetExportFinancialAsync() => await ExportFinancialAsync();
        public async Task<IActionResult> OnPostExportFinancialAsync() => await ExportFinancialAsync();

        private async Task<IActionResult> ExportFinancialAsync()
        {
            await LoadMonthAsync();

            string finFingerprint = $"FinancialExport_{SelectedMonth}_{MonthTotalReceipts}_{MonthTotalDisbursements}";

            var logQuery = await _supabase.Client.From<ExportLogRecord>().Filter("report_month", Operator.Equals, SelectedMonth).Get();
            var existingLog = logQuery.Models.FirstOrDefault();

            if (existingLog != null && existingLog.FinancialFingerprint == finFingerprint)
            {
                Message = "Your Financial Report is already up to date in Google Sheets! No new changes detected.";
                return Page();
            }

            await _supabase.InitializeAsync(true);

            if (string.IsNullOrWhiteSpace(SelectedMonth)) SelectedMonth = DateTime.Today.ToString("yyyy-MM");
            if (!DateTime.TryParse($"{SelectedMonth}-01", out var monthStart)) monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);

            var firstDay = new DateTime(monthStart.Year, monthStart.Month, 1);
            var lastDay = firstDay.AddMonths(1).AddDays(-1);

            var previousMonthStr = firstDay.AddMonths(-1).ToString("yyyy-MM");
            var prevBalancesResponse = await _supabase.Client.From<MonthlyFundBalance>().Filter("report_month", Operator.Equals, previousMonthStr).Get();
            var beginningBalances = prevBalancesResponse.Models.ToList();

            var givingResponse = await _supabase.Client.From<GivingRecord>()
                .Filter("service_date", Operator.GreaterThanOrEqual, firstDay.ToString("yyyy-MM-dd"))
                .Filter("service_date", Operator.LessThanOrEqual, lastDay.ToString("yyyy-MM-dd")).Get();
            var givingRecords = givingResponse.Models.ToList();

            // N+1 FIX: Bulk fetch all Entries, Records, Vouchers, and Items for the entire month
            var givingRecordIds = givingRecords.Select(g => (object)g.Id).ToList();

            var allGivingEntries = new List<GivingEntry>();
            if (givingRecordIds.Any())
            {
                var entryResp = await _supabase.Client.From<GivingEntry>().Filter("giving_record_id", Operator.In, givingRecordIds).Get();
                allGivingEntries = entryResp.Models.ToList();
            }

            var disbRecords = new List<DisbursementRecord>();
            if (givingRecordIds.Any())
            {
                var dResp = await _supabase.Client.From<DisbursementRecord>().Filter("giving_record_id", Operator.In, givingRecordIds).Get();
                disbRecords.AddRange(dResp.Models);
            }

            var strayDisbResp = await _supabase.Client.From<DisbursementRecord>()
                .Filter("record_date", Operator.GreaterThanOrEqual, firstDay.ToString("yyyy-MM-dd"))
                .Filter("record_date", Operator.LessThanOrEqual, lastDay.ToString("yyyy-MM-dd")).Get();

            foreach (var sd in strayDisbResp.Models)
                if (!disbRecords.Any(d => d.Id == sd.Id)) disbRecords.Add(sd);

            var disbRecordIds = disbRecords.Select(d => (object)d.Id).ToList();
            var allVouchers = new List<Voucher>();
            if (disbRecordIds.Any())
            {
                var vResp = await _supabase.Client.From<Voucher>().Filter("disbursement_record_id", Operator.In, disbRecordIds).Get();
                allVouchers = vResp.Models.ToList();
            }

            var voucherIds = allVouchers.Select(v => (object)v.Id).ToList();
            var allVoucherItems = new List<VoucherItem>();
            if (voucherIds.Any())
            {
                var viResp = await _supabase.Client.From<VoucherItem>().Filter("voucher_id", Operator.In, voucherIds).Get();
                allVoucherItems = viResp.Models.ToList();
            }

            var transactions = new List<LedgerTransaction>();

            foreach (var gr in givingRecords)
            {
                var entries = allGivingEntries.Where(e => e.GivingRecordId == gr.Id).ToList();
                transactions.Add(new LedgerTransaction
                {
                    Date = gr.ServiceDate,
                    Particulars = "Tithes & Offerings",
                    ReceiptAmount = gr.GrandTotal,
                    GeneralFund = entries.Sum(e => e.Tithes + e.Offerings),
                    Mission = entries.Sum(e => e.Mission),
                    Solomon = entries.Sum(e => e.Solomon),
                    Noah = entries.Sum(e => e.Noah)
                });
            }

            foreach (var dr in disbRecords)
            {
                var vouchers = allVouchers.Where(v => v.DisbursementRecordId == dr.Id).ToList();

                foreach (var v in vouchers)
                {
                    var items = allVoucherItems.Where(i => i.VoucherId == v.Id).ToList();

                    string ministryRaw = (v.Ministry ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(ministryRaw)) ministryRaw = "General";
                    string ministryName = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(ministryRaw.ToLower());

                    foreach (var item in items)
                    {
                        var netAmount = item.Amount - item.AmountReturned;
                        if (netAmount <= 0) continue;

                        var rawParticulars = (item.Particular ?? "").Trim();
                        if (string.IsNullOrWhiteSpace(rawParticulars))
                            rawParticulars = $"Miscellaneous";

                        var lowerParticulars = rawParticulars.ToLower();
                        var safePayee = (v.Payee ?? "").Trim();
                        var lowerPayee = safePayee.ToLower();

                        string finalParticulars = rawParticulars;

                        if (lowerParticulars.Contains("professional fee") &&
                            (lowerPayee.Contains("imelda") || lowerParticulars.Contains("imelda")))
                        {
                            finalParticulars = "Internal auditor's professional fee - Sis. Imelda";
                        }
                        else if (lowerParticulars.Contains("meralco") || lowerParticulars.Contains("laguna water"))
                        {
                            finalParticulars = lowerParticulars.StartsWith("utilities") ? rawParticulars : $"Utilities: {rawParticulars}";
                        }
                        else if (lowerParticulars.StartsWith("sss"))
                        {
                            finalParticulars = string.IsNullOrWhiteSpace(safePayee)
                                ? "SSS Contribution"
                                : $"SSS Contribution - {safePayee}";
                        }
                        else if (lowerParticulars.Contains("pfc") && lowerParticulars.Contains("dela paz"))
                        {
                            finalParticulars = "PFC - Dela Paz";
                        }
                        else if (lowerParticulars.Contains("pfc") && lowerParticulars.Contains("olivarez"))
                        {
                            finalParticulars = "PFC - Olivarez";
                        }
                        else if (lowerParticulars.Contains("stipend") || lowerParticulars.Contains("professional fee"))
                        {
                            if (!string.IsNullOrWhiteSpace(safePayee))
                            {
                                finalParticulars = $"{rawParticulars} - {safePayee}";
                            }
                        }

                        var t = new LedgerTransaction
                        {
                            Date = dr.RecordDate,
                            Particulars = finalParticulars,
                            DisbursementAmount = netAmount,
                            Ministry = ministryName,
                            FundSource = item.FundSource
                        };

                        transactions.Add(t);
                    }
                }
            }

            transactions = transactions.OrderBy(t => t.Date).ToList();

            var monthGivingRecordIds = givingRecords.Select(r => r.Id.ToString()).ToList();
            var monthGivingEntries = allGivingEntries.Where(e => monthGivingRecordIds.Contains(e.GivingRecordId.ToString())).ToList();

            decimal begGen = beginningBalances.FirstOrDefault(b => b.FundName == "General")?.EndingBalance ?? 0;
            decimal begPledges = beginningBalances.FirstOrDefault(b => b.FundName == "Pledges")?.EndingBalance ?? 0;
            decimal begConst = beginningBalances.FirstOrDefault(b => b.FundName == "Construction")?.EndingBalance ?? 0;
            decimal begPW = beginningBalances.FirstOrDefault(b => b.FundName == "Praise & Worship")?.EndingBalance ?? 0;

            decimal incPledges = monthGivingEntries.Sum(e => e.Solomon + e.Noah + e.Mission);
            decimal incGen = monthGivingEntries.Sum(e => e.Tithes + e.Offerings) + monthGivingEntries.Where(e => e.OthersFund == "General" || string.IsNullOrWhiteSpace(e.OthersFund)).Sum(e => e.Others);
            decimal incConst = monthGivingEntries.Where(e => e.OthersFund == "Construction").Sum(e => e.Others);
            decimal incPW = monthGivingEntries.Where(e => e.OthersFund == "Praise & Worship").Sum(e => e.Others);

            decimal expConst = transactions.Where(t => t.FundSource == "Construction").Sum(t => t.DisbursementAmount);
            decimal expPW = transactions.Where(t => t.FundSource == "Praise & Worship").Sum(t => t.DisbursementAmount);
            decimal expPledges = transactions.Where(t => t.FundSource == "Pledges").Sum(t => t.DisbursementAmount);
            decimal expGen = transactions.Where(t => t.FundSource == "General").Sum(t => t.DisbursementAmount);

            var endBalances = new List<MonthlyFundBalance>
            {
                new MonthlyFundBalance { ReportMonth = SelectedMonth, FundName = "General", Custodian = "Sis. Cora", EndingBalance = begGen + incGen - expGen },
                new MonthlyFundBalance { ReportMonth = SelectedMonth, FundName = "Pledges", Custodian = "Sis. Cora", EndingBalance = begPledges + incPledges - expPledges },
                new MonthlyFundBalance { ReportMonth = SelectedMonth, FundName = "Construction", Custodian = "Ptra Es", EndingBalance = begConst + incConst - expConst },
                new MonthlyFundBalance { ReportMonth = SelectedMonth, FundName = "Praise & Worship", Custodian = "P/W", EndingBalance = begPW + incPW - expPW }
            };

            // N+1 FIX: Fetch all existing balances for the month once, then Bulk Upsert
            var existingBalsResp = await _supabase.Client.From<MonthlyFundBalance>()
                .Filter("report_month", Operator.Equals, SelectedMonth)
                .Get();
            var existingBals = existingBalsResp.Models;

            var balsToUpdate = new List<MonthlyFundBalance>();
            var balsToInsert = new List<MonthlyFundBalance>();

            foreach (var bal in endBalances)
            {
                var existing = existingBals.FirstOrDefault(b => b.FundName == bal.FundName);
                if (existing != null)
                {
                    existing.EndingBalance = bal.EndingBalance;
                    existing.Custodian = bal.Custodian;
                    balsToUpdate.Add(existing);
                }
                else
                {
                    balsToInsert.Add(bal);
                }
            }

            if (balsToUpdate.Any()) await _supabase.Client.From<MonthlyFundBalance>().Upsert(balsToUpdate);
            if (balsToInsert.Any()) await _supabase.Client.From<MonthlyFundBalance>().Insert(balsToInsert);

            // ====================================================================
            // SAVE THE MONTHLY PLEDGE BREAKDOWN
            // ====================================================================
            var prevBreakdownResp = await _supabase.Client.From<MonthlyPledgeBreakdown>()
                .Filter("report_month", Operator.Equals, previousMonthStr).Get();
            var prevBreakdown = prevBreakdownResp.Models.FirstOrDefault();

            decimal begSolomon = prevBreakdown?.SolomonBalance ?? 0;
            decimal begNoah = prevBreakdown?.NoahBalance ?? 0;
            decimal begMission = prevBreakdown?.MissionBalance ?? 0;
            decimal begOthers = prevBreakdown?.OthersBalance ?? 0;

            decimal incSolomonRaw = monthGivingEntries.Sum(e => e.Solomon);
            decimal incNoahRaw = monthGivingEntries.Sum(e => e.Noah);
            decimal incMissionRaw = monthGivingEntries.Sum(e => e.Mission);
            decimal incOtherPledgesRaw = monthGivingEntries.Where(e => e.OthersFund == "Pledges").Sum(e => e.Others);

            var endBreakdown = new MonthlyPledgeBreakdown
            {
                ReportMonth = SelectedMonth,
                SolomonBalance = begSolomon + incSolomonRaw,
                NoahBalance = begNoah + incNoahRaw,
                MissionBalance = begMission + incMissionRaw,
                OthersBalance = begOthers + incOtherPledgesRaw
            };

            var existingBreakdownQuery = await _supabase.Client.From<MonthlyPledgeBreakdown>()
                .Filter("report_month", Operator.Equals, SelectedMonth).Get();
            var existingBreakdown = existingBreakdownQuery.Models.FirstOrDefault();

            if (existingBreakdown != null)
            {
                existingBreakdown.SolomonBalance = endBreakdown.SolomonBalance;
                existingBreakdown.NoahBalance = endBreakdown.NoahBalance;
                existingBreakdown.MissionBalance = endBreakdown.MissionBalance;
                existingBreakdown.OthersBalance = endBreakdown.OthersBalance;
                await _supabase.Client.From<MonthlyPledgeBreakdown>().Update(existingBreakdown);
            }
            else
            {
                await _supabase.Client.From<MonthlyPledgeBreakdown>().Insert(endBreakdown);
            }

            decimal totalIncomeCheck = givingRecords.Sum(g => g.GrandTotal);
            decimal totalDisbCheck = allVoucherItems.Sum(i => i.Amount - i.AmountReturned);


            string credentialPath = "google-credentials.json";
            string spreadsheetId = AppSettings.FinancialSheetId;


            GoogleCredential credential;
            using (var stream = new FileStream(credentialPath, FileMode.Open, FileAccess.Read))
            {
#pragma warning disable CS0618
                credential = GoogleCredential.FromStream(stream).CreateScoped(SheetsService.Scope.Spreadsheets);
#pragma warning restore CS0618
            }

            var service = new SheetsService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = "FinanceApp"
            });

            var tabName = firstDay.ToString("MMMM yyyy").ToUpper();
            var spreadsheet = await service.Spreadsheets.Get(spreadsheetId).ExecuteAsync();
            var existingSheet = spreadsheet.Sheets.FirstOrDefault(s => s.Properties.Title == tabName);

            int targetIndex = 0;
            foreach (var s in spreadsheet.Sheets)
            {
                if (s.Properties.Title == tabName) continue;

                if (DateTime.TryParseExact(s.Properties.Title, "MMMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var sheetDate))
                {
                    if (monthStart < sheetDate)
                    {
                        break;
                    }
                }
                targetIndex++;
            }

            var batchRequests = new List<Request>();

            if (existingSheet != null)
            {
                int oldSheetId = existingSheet.Properties.SheetId ?? 0;
                batchRequests.Add(new Request
                {
                    DeleteSheet = new DeleteSheetRequest { SheetId = oldSheetId }
                });
            }

            batchRequests.Add(new Request
            {
                AddSheet = new AddSheetRequest
                {
                    Properties = new SheetProperties
                    {
                        Title = tabName,
                        Index = targetIndex,
                        GridProperties = new GridProperties
                        {
                            HideGridlines = true
                        }
                    }
                }
            });

            var batchResponse = await service.Spreadsheets.BatchUpdate(
                new BatchUpdateSpreadsheetRequest { Requests = batchRequests },
                spreadsheetId).ExecuteAsync();

            var addSheetReply = batchResponse.Replies.FirstOrDefault(r => r.AddSheet != null)?.AddSheet;
            int sheetId = addSheetReply?.Properties.SheetId ?? 0;

            List<List<object>> grid = new List<List<object>>();
            List<Request> formatRequests = new List<Request>();

            formatRequests.Add(new Request
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = new GridRange { SheetId = sheetId, StartRowIndex = 0, EndRowIndex = 500, StartColumnIndex = 0, EndColumnIndex = 25 },
                    Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { FontFamily = "Arial", FontSize = 11 } } },
                    Fields = "userEnteredFormat.textFormat.fontFamily,userEnteredFormat.textFormat.fontSize"
                }
            });

            BuildFinancialReportGrid(grid, formatRequests, sheetId, firstDay, beginningBalances, transactions, monthGivingEntries, endBalances);

            IList<IList<object>> allRows = grid.Select(r => (IList<object>)r).ToList();
            var valueRange = new ValueRange { Values = allRows };
            var updateRequest = service.Spreadsheets.Values.Update(valueRange, spreadsheetId, $"{tabName}!A1");
            updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
            await updateRequest.ExecuteAsync();

            if (formatRequests.Any())
            {
                await service.Spreadsheets.BatchUpdate(new BatchUpdateSpreadsheetRequest { Requests = formatRequests }, spreadsheetId).ExecuteAsync();
            }


            var finExportOptions = new MemoryCacheEntryOptions()
                .SetSize(1) 
                .SetAbsoluteExpiration(TimeSpan.FromDays(7));
            _cache.Set(finFingerprint, true, finExportOptions);

            var logToSave = existingLog ?? new ExportLogRecord { ReportMonth = SelectedMonth };
            logToSave.FinancialFingerprint = finFingerprint;
            await _supabase.Client.From<ExportLogRecord>().Upsert(logToSave);

            await LoadMonthAsync();
            Message = $"Successfully exported Financial Report: {tabName}!";
            return Page();
        }

        private string GetBaseName(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return "";

            string lower = label.ToLower();

            // Client Exception: Leave PFC and Rent exactly as they are currently formatted
            if (lower.Contains("pfc - olivarez") || lower.Contains("rent payment") || lower.Contains("pfc - dela paz"))
            {
                return label.Trim();
            }

            // Split by " - " or ": " to find the base category
            int dashIndex = label.IndexOf(" - ");
            int colonIndex = label.IndexOf(": ");

            int splitIndex = -1;
            if (dashIndex >= 0 && colonIndex >= 0) splitIndex = Math.Min(dashIndex, colonIndex);
            else if (dashIndex >= 0) splitIndex = dashIndex;
            else if (colonIndex >= 0) splitIndex = colonIndex;

            if (splitIndex > 0)
            {
                return label.Substring(0, splitIndex).Trim();
            }

            return label.Trim();
        }

        private void BuildFinancialReportGrid(
            List<List<object>> grid, List<Request> formats, int sheetId,
            DateTime month, IReadOnlyList<MonthlyFundBalance> beginningBalances,
            List<LedgerTransaction> transactions, List<GivingEntry> givingEntries,
            List<MonthlyFundBalance> endBalances)
        {
            var lastDay = new DateTime(month.Year, month.Month,
                DateTime.DaysInMonth(month.Year, month.Month));

            decimal totalBegBalance = beginningBalances.Sum(b => b.EndingBalance);
            decimal totalReceipts = transactions.Sum(t => t.ReceiptAmount);
            decimal totalDisb = transactions.Sum(t => t.DisbursementAmount);

            int row = 1;
            SetCell(grid, row, 1, "ADELINA CHRISTIAN CHURCH");
            SetCell(grid, row + 1, 1, "Financial Report");
            SetCell(grid, row + 2, 1, lastDay.ToString("MMMM dd, yyyy"));

            row = 5; SetCell(grid, row, 8, "AMOUNT");
            row = 6; SetCell(grid, row, 1, "BEGINNING BALANCE");
            SetCell(grid, row, 8, totalBegBalance);
            row = 7; SetCell(grid, row, 1, "ADD: CASH RECEIPTS / BLESSINGS");

            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 1, EndRowIndex = 2, StartColumnIndex = 1, EndColumnIndex = 9 }, MergeType = "MERGE_ALL" } });
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 2, EndRowIndex = 3, StartColumnIndex = 1, EndColumnIndex = 9 }, MergeType = "MERGE_ALL" } });
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 3, EndRowIndex = 4, StartColumnIndex = 1, EndColumnIndex = 9 }, MergeType = "MERGE_ALL" } });
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 6, EndRowIndex = 7, StartColumnIndex = 1, EndColumnIndex = 4 }, MergeType = "MERGE_ALL" } });
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 7, EndRowIndex = 8, StartColumnIndex = 1, EndColumnIndex = 4 }, MergeType = "MERGE_ALL" } });
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 1, EndRowIndex = 4, StartColumnIndex = 1, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { HorizontalAlignment = "CENTER" } }, Fields = "userEnteredFormat.horizontalAlignment" } });
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 1, EndRowIndex = 3, StartColumnIndex = 1, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } }, Fields = "userEnteredFormat.textFormat.bold" } });
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 5, EndRowIndex = 6, StartColumnIndex = 8, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true }, HorizontalAlignment = "RIGHT" } }, Fields = "userEnteredFormat.textFormat.bold,userEnteredFormat.horizontalAlignment" } });
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 6, EndRowIndex = 7, StartColumnIndex = 1, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } }, Fields = "userEnteredFormat.textFormat.bold" } });
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 6, EndRowIndex = 7, StartColumnIndex = 8, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { NumberFormat = new NumberFormat { Type = "CURRENCY", Pattern = "\"₱\"#,##0.00" } } }, Fields = "userEnteredFormat.numberFormat" } });
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 7, EndRowIndex = 8, StartColumnIndex = 1, EndColumnIndex = 4 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } }, Fields = "userEnteredFormat.textFormat.bold" } });

            row = 8;
            SetCell(grid, row, 2, "Sources of Blessings:");
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = row, EndRowIndex = row + 1, StartColumnIndex = 2, EndColumnIndex = 4 }, MergeType = "MERGE_ALL" } });
            row++;

            decimal valTithes = givingEntries.Sum(e => e.Tithes);
            decimal valOfferings = givingEntries.Sum(e => e.Offerings);
            decimal valSolomon = givingEntries.Sum(e => e.Solomon);
            decimal valNoah = givingEntries.Sum(e => e.Noah);
            decimal valMission = givingEntries.Sum(e => e.Mission);

            int tithesRow = row;
            SetCell(grid, row, 3, "Tithes"); SetCell(grid, row, 8, valTithes); row++;
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = tithesRow, EndRowIndex = tithesRow + 1, StartColumnIndex = 8, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } }, Fields = "userEnteredFormat.textFormat.bold" } });

            SetCell(grid, row, 3, "Offerings"); SetCell(grid, row, 8, valOfferings); row++;
            SetCell(grid, row, 3, "Pledges:"); row++;
            SetCell(grid, row, 3, "    Solomon"); SetCell(grid, row, 7, valSolomon); row++;

            int noahRow = row;
            SetCell(grid, row, 3, "    Noah"); SetCell(grid, row, 7, valNoah);
            SetCell(grid, row, 8, valSolomon + valNoah); row++;

            SetCell(grid, row, 3, "Mission"); SetCell(grid, row, 8, valMission); row++;
            var othersList = givingEntries.Where(e => e.Others > 0).ToList();
            decimal othersTotal = 0;

            if (othersList.Any())
            {
                SetCell(grid, row, 3, "Others"); row++;
                foreach (var item in othersList)
                {
                    string lbl = !string.IsNullOrWhiteSpace(item.EntryName)
                        ? item.EntryName : "(Unnamed Entry)";
                    SetCell(grid, row, 3, "    " + lbl);
                    SetCell(grid, row, 7, item.Others);
                    othersTotal += item.Others;
                    row++;
                }
                int lastOthersRow = row - 1;
                SetCell(grid, lastOthersRow, 8, othersTotal);
            }

            int totalReceiptRow = row;
            SetCell(grid, totalReceiptRow, 1, "Total");
            SetCell(grid, totalReceiptRow, 8, totalBegBalance + totalReceipts);
            row++;

            formats.Add(new Request { UpdateBorders = new UpdateBordersRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = noahRow, EndRowIndex = noahRow + 1, StartColumnIndex = 7, EndColumnIndex = 8 }, Bottom = new Border { Style = "SOLID", Color = new Color { Red = 0f, Green = 0f, Blue = 0f } } } });

            formats.Add(new Request { UpdateBorders = new UpdateBordersRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = totalReceiptRow, EndRowIndex = totalReceiptRow + 1, StartColumnIndex = 8, EndColumnIndex = 9 }, Top = new Border { Style = "SOLID", Color = new Color { Red = 0f, Green = 0f, Blue = 0f } } } });

            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 8, EndRowIndex = totalReceiptRow, StartColumnIndex = 7, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { NumberFormat = new NumberFormat { Type = "NUMBER", Pattern = "#,##0.00" } } }, Fields = "userEnteredFormat.numberFormat" } });
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 8, EndRowIndex = totalReceiptRow, StartColumnIndex = 8, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } }, Fields = "userEnteredFormat.textFormat.bold" } });
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = totalReceiptRow, EndRowIndex = totalReceiptRow + 1, StartColumnIndex = 1, EndColumnIndex = 2 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } }, Fields = "userEnteredFormat.textFormat.bold" } });
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = totalReceiptRow, EndRowIndex = totalReceiptRow + 1, StartColumnIndex = 8, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { BackgroundColor = new Color { Red = 0.60f, Green = 0.80f, Blue = 0.53f }, TextFormat = new TextFormat { Bold = true, ForegroundColor = new Color { Red = 0f, Green = 0f, Blue = 0f } }, NumberFormat = new NumberFormat { Type = "CURRENCY", Pattern = "#,##0.00" } } }, Fields = "userEnteredFormat.backgroundColor,userEnteredFormat.textFormat.bold,userEnteredFormat.textFormat.foregroundColor,userEnteredFormat.numberFormat" } });

            row += 2;
            SetCell(grid, row, 1, "LESS: DISBURSEMENTS");
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = row, EndRowIndex = row + 1, StartColumnIndex = 1, EndColumnIndex = 9 }, MergeType = "MERGE_ALL" } });
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = row, EndRowIndex = row + 1, StartColumnIndex = 1, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } }, Fields = "userEnteredFormat.textFormat.bold" } });
            row++;

            var ministryGroups = transactions
                .Where(t => t.DisbursementAmount > 0)
                .GroupBy(t => (t.Ministry ?? "General").Trim())
                .OrderBy(g => g.Key)
                .ToList();

            int firstDisbDataRow = row;

            foreach (var ministryGroup in ministryGroups)
            {
                var summarized = ministryGroup
                    .GroupBy(t => t.Particulars.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(g => (Label: g.Key, Amount: g.Sum(x => x.DisbursementAmount)))
                    .Where(x => x.Amount > 0)
                    .OrderBy(x => x.Label)
                    .ToList();

                if (!summarized.Any()) continue;

                var olivarezIdx = summarized.FindIndex(x => x.Label.Equals("PFC - Olivarez", StringComparison.OrdinalIgnoreCase));
                if (olivarezIdx >= 0)
                {
                    var olivarez = summarized[olivarezIdx];
                    if (olivarez.Amount >= 2000m)
                    {
                        summarized.RemoveAt(olivarezIdx);
                        summarized.Add((Label: "Rent payment - PFC Olivarez", Amount: 2000m));
                        if (olivarez.Amount > 2000m)
                        {
                            summarized.Add((Label: "PFC - Olivarez", Amount: olivarez.Amount - 2000m));
                        }
                        summarized = summarized.OrderBy(x => x.Label).ToList();
                    }
                }

                string ministryLabel = ministryGroup.Key.Trim();
                if (!ministryLabel.EndsWith("Expenses", StringComparison.OrdinalIgnoreCase))
                    ministryLabel += " Expenses";

                int ministryHeaderRow = row;
                SetCell(grid, ministryHeaderRow, 2, $"{ministryLabel}:");
                formats.Add(new Request
                {
                    MergeCells = new MergeCellsRequest
                    {
                        Range = new GridRange { SheetId = sheetId, StartRowIndex = ministryHeaderRow, EndRowIndex = ministryHeaderRow + 1, StartColumnIndex = 2, EndColumnIndex = 9 },
                        MergeType = "MERGE_ALL"
                    }
                });
                row++;

                // ====================================================================
                // 🚨 NEW DYNAMIC SUB-GROUPING LOGIC 🚨
                // Automatically groups ANY particulars sharing the same base name 
                // (e.g. 'Cleaning materials' and 'Cleaning materials - mop')
                // ====================================================================
                decimal ministryTotal = summarized.Sum(x => x.Amount);

                if (summarized.Count == 1)
                {
                    // Only 1 item in the whole ministry (Bypasses Column H)
                    SetCell(grid, row, 3, summarized[0].Label);
                    SetCell(grid, row, 8, ministryTotal);
                    row++;
                }
                else
                {
                    // Group items by their Base Name AND force multi-item groups to the top!
                    var subGroups = summarized
                        .GroupBy(x => GetBaseName(x.Label))
                        .OrderByDescending(g => g.Count() > 1 ? 1 : 0) // Multi-item groups (1) come before single items (0)
                        .ThenBy(g => g.Key) // Then sort alphabetically by the base name
                        .ToList();

                    int lastMinistryRow = -1;

                    for (int gIdx = 0; gIdx < subGroups.Count; gIdx++)
                    {
                        var currentGroup = subGroups[gIdx].ToList();
                        bool isLastGroup = gIdx == subGroups.Count - 1;

                        if (currentGroup.Count > 1)
                        {
                            // MULTI-ITEM GROUP (Formats like Stipend: Indiv in Col G, Sum in Col H)
                            decimal subTotal = 0;
                            int lastSubRow = -1;

                            foreach (var item in currentGroup)
                            {
                                SetCell(grid, row, 3, item.Label);
                                SetCell(grid, row, 6, item.Amount); // Col G
                                subTotal += item.Amount;
                                lastSubRow = row;
                                row++;
                            }

                            SetCell(grid, lastSubRow, 7, subTotal); // Col H

                            // Line under the last Col G amount
                            formats.Add(new Request { UpdateBorders = new UpdateBordersRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = lastSubRow, EndRowIndex = lastSubRow + 1, StartColumnIndex = 6, EndColumnIndex = 7 }, Bottom = new Border { Style = "SOLID", Color = new Color { Red = 0f, Green = 0f, Blue = 0f } } } });

                            lastMinistryRow = lastSubRow;
                        }
                        else
                        {
                            // SINGLE-ITEM GROUP (Formats normally: Item direct to Col H)
                            var item = currentGroup[0];
                            SetCell(grid, row, 3, item.Label);
                            SetCell(grid, row, 7, item.Amount); // Col H
                            lastMinistryRow = row;
                            row++;
                        }

                        // If this is the absolute LAST item in the entire Ministry, put the Grand Total in Col I
                        if (isLastGroup)
                        {
                            SetCell(grid, lastMinistryRow, 8, ministryTotal);

                            // Line under the last Col H amount
                            formats.Add(new Request { UpdateBorders = new UpdateBordersRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = lastMinistryRow, EndRowIndex = lastMinistryRow + 1, StartColumnIndex = 7, EndColumnIndex = 8 }, Bottom = new Border { Style = "SOLID", Color = new Color { Red = 0f, Green = 0f, Blue = 0f } } } });
                        }
                    }
                }
            }

            int totalDisbRow = row;
            SetCell(grid, totalDisbRow, 1, "Total");
            SetCell(grid, totalDisbRow, 8, totalDisb);
            row++;

            if (firstDisbDataRow < totalDisbRow)
            {
                formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = firstDisbDataRow, EndRowIndex = totalDisbRow, StartColumnIndex = 8, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } }, Fields = "userEnteredFormat.textFormat.bold" } });
                formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = firstDisbDataRow, EndRowIndex = totalDisbRow + 1, StartColumnIndex = 6, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { NumberFormat = new NumberFormat { Type = "NUMBER", Pattern = "#,##0.00" } } }, Fields = "userEnteredFormat.numberFormat" } });
            }

            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = totalDisbRow, EndRowIndex = totalDisbRow + 1, StartColumnIndex = 1, EndColumnIndex = 2 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } }, Fields = "userEnteredFormat.textFormat.bold" } });
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = totalDisbRow, EndRowIndex = totalDisbRow + 1, StartColumnIndex = 8, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { BackgroundColor = new Color { Red = 0.98f, Green = 0.8f, Blue = 0.8f }, TextFormat = new TextFormat { Bold = true, ForegroundColor = new Color { Red = 0f, Green = 0f, Blue = 0f } } } }, Fields = "userEnteredFormat.backgroundColor,userEnteredFormat.textFormat.bold,userEnteredFormat.textFormat.foregroundColor" } });
            formats.Add(new Request { UpdateBorders = new UpdateBordersRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = totalDisbRow, EndRowIndex = totalDisbRow + 1, StartColumnIndex = 8, EndColumnIndex = 9 }, Top = new Border { Style = "SOLID", Color = new Color { Red = 0f, Green = 0f, Blue = 0f } } } });


            // NET CASH BALANCE & WALLET BREAKDOWN
            row += 2;
            int netStartRow = row;

            decimal finalNetCashBalance = totalBegBalance + totalReceipts - totalDisb;

            SetCell(grid, row, 1, "NET CASH BALANCE");
            SetCell(grid, row, 8, finalNetCashBalance);
            row++;

            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = netStartRow, EndRowIndex = netStartRow + 1, StartColumnIndex = 1, EndColumnIndex = 6 }, MergeType = "MERGE_ALL" } });
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = netStartRow, EndRowIndex = netStartRow + 1, StartColumnIndex = 1, EndColumnIndex = 2 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } }, Fields = "userEnteredFormat.textFormat.bold" } });

            var netBgColor = finalNetCashBalance < 0 ? new Color { Red = 0.98f, Green = 0.8f, Blue = 0.8f } : new Color { Red = 0.60f, Green = 0.80f, Blue = 0.53f };

            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = netStartRow, EndRowIndex = netStartRow + 1, StartColumnIndex = 8, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { BackgroundColor = netBgColor, TextFormat = new TextFormat { Bold = true, ForegroundColor = new Color { Red = 0f, Green = 0f, Blue = 0f } }, NumberFormat = new NumberFormat { Type = "NUMBER", Pattern = "#,##0.00;(#,##0.00)" } } }, Fields = "userEnteredFormat.backgroundColor,userEnteredFormat.textFormat.bold,userEnteredFormat.textFormat.foregroundColor,userEnteredFormat.numberFormat" } });

            var genBal = endBalances.FirstOrDefault(b => b.FundName == "General")?.EndingBalance ?? 0;
            var pldgBal = endBalances.FirstOrDefault(b => b.FundName == "Pledges")?.EndingBalance ?? 0;
            var constBal = endBalances.FirstOrDefault(b => b.FundName == "Construction")?.EndingBalance ?? 0;
            var pwBal = endBalances.FirstOrDefault(b => b.FundName == "Praise & Worship")?.EndingBalance ?? 0;

            int walletStartRow = row;

            SetCell(grid, row, 3, "Pledges - c/o Sis. Cora");
            SetCell(grid, row, 6, pldgBal);
            row++;

            SetCell(grid, row, 3, "General Fund - c/o Sis. Cora");
            SetCell(grid, row, 6, genBal);
            row++;

            SetCell(grid, row, 3, "For church ceiling construction - c./o Ptra Es");
            SetCell(grid, row, 6, constBal);
            row++;

            int pwRow = row;
            SetCell(grid, row, 3, "Fund - c/o P/W");
            SetCell(grid, row, 6, pwBal);
            row++;

            if (walletStartRow < row)
            {
                for (int i = walletStartRow; i < row; i++)
                {
                    formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = i, EndRowIndex = i + 1, StartColumnIndex = 3, EndColumnIndex = 6 }, MergeType = "MERGE_ALL" } });
                }
            }

            if (netStartRow + 1 < row)
            {
                formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = netStartRow + 1, EndRowIndex = row, StartColumnIndex = 6, EndColumnIndex = 7 }, Cell = new CellData { UserEnteredFormat = new CellFormat { NumberFormat = new NumberFormat { Type = "NUMBER", Pattern = "#,##0.00;(#,##0.00)" } } }, Fields = "userEnteredFormat.numberFormat" } });
            }

            // SIGNATURE LINES
            row += 4;
            int headerRow1 = row;

            // Row 1: Titles
            SetCell(grid, headerRow1, 3, "Prepared by:"); // Column D (Index 3)
            SetCell(grid, headerRow1, 5, "Noted by:");    // Column F (Index 5)
            SetCell(grid, headerRow1, 7, "Audited by:");  // Column H (Index 7)

            // Add 2 empty rows, place names in the 3rd
            int namesRow1 = headerRow1 + 3;
            SetCell(grid, namesRow1, 3, "Hanna Guigayoma");
            SetCell(grid, namesRow1, 5, "Cora Malaga");
            SetCell(grid, namesRow1, 7, "Imelda Marcilla");

            // Directly below the names
            int posRow1 = namesRow1 + 1;
            SetCell(grid, posRow1, 3, "Assistant Treasurer");
            SetCell(grid, posRow1, 5, "Treasurer");
            SetCell(grid, posRow1, 7, "Auditor");

            // Add 2 empty rows, place "Approved by:"
            int headerRow2 = posRow1 + 3;
            SetCell(grid, headerRow2, 5, "Approved by:");

            // Add 2 empty rows, place the final name
            int namesRow2 = headerRow2 + 3;
            SetCell(grid, namesRow2, 5, "Ptr. Sydney Herrera");

            // Directly below the name
            int posRow2 = namesRow2 + 1;
            SetCell(grid, posRow2, 5, "Senior Pastor");

            // Update row variable to the last row used
            row = posRow2;
            int finalRow = row;

            // Merge Cells
            // Header 1
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = headerRow1, EndRowIndex = headerRow1 + 1, StartColumnIndex = 3, EndColumnIndex = 5 }, MergeType = "MERGE_ALL" } });
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = headerRow1, EndRowIndex = headerRow1 + 1, StartColumnIndex = 5, EndColumnIndex = 7 }, MergeType = "MERGE_ALL" } });
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = headerRow1, EndRowIndex = headerRow1 + 1, StartColumnIndex = 7, EndColumnIndex = 9 }, MergeType = "MERGE_ALL" } });

            // Names 1
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = namesRow1, EndRowIndex = namesRow1 + 1, StartColumnIndex = 3, EndColumnIndex = 5 }, MergeType = "MERGE_ALL" } });
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = namesRow1, EndRowIndex = namesRow1 + 1, StartColumnIndex = 5, EndColumnIndex = 7 }, MergeType = "MERGE_ALL" } });
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = namesRow1, EndRowIndex = namesRow1 + 1, StartColumnIndex = 7, EndColumnIndex = 9 }, MergeType = "MERGE_ALL" } });

            // Pos 1
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = posRow1, EndRowIndex = posRow1 + 1, StartColumnIndex = 3, EndColumnIndex = 5 }, MergeType = "MERGE_ALL" } });
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = posRow1, EndRowIndex = posRow1 + 1, StartColumnIndex = 5, EndColumnIndex = 7 }, MergeType = "MERGE_ALL" } });
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = posRow1, EndRowIndex = posRow1 + 1, StartColumnIndex = 7, EndColumnIndex = 9 }, MergeType = "MERGE_ALL" } });

            // Header 2
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = headerRow2, EndRowIndex = headerRow2 + 1, StartColumnIndex = 5, EndColumnIndex = 7 }, MergeType = "MERGE_ALL" } });

            // Names 2
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = namesRow2, EndRowIndex = namesRow2 + 1, StartColumnIndex = 5, EndColumnIndex = 7 }, MergeType = "MERGE_ALL" } });

            // Pos 2
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = posRow2, EndRowIndex = posRow2 + 1, StartColumnIndex = 5, EndColumnIndex = 7 }, MergeType = "MERGE_ALL" } });

            // Format: Center align the entire signature block columns
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = headerRow1, EndRowIndex = finalRow + 1, StartColumnIndex = 3, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { HorizontalAlignment = "CENTER" } }, Fields = "userEnteredFormat.horizontalAlignment" } });

            // Format: Bold Headers
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = headerRow1, EndRowIndex = headerRow1 + 1, StartColumnIndex = 3, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } }, Fields = "userEnteredFormat.textFormat.bold" } });
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = headerRow2, EndRowIndex = headerRow2 + 1, StartColumnIndex = 5, EndColumnIndex = 7 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } }, Fields = "userEnteredFormat.textFormat.bold" } });

            // Format: Normal Names
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = namesRow1, EndRowIndex = namesRow1 + 1, StartColumnIndex = 3, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = false } } }, Fields = "userEnteredFormat.textFormat.bold" } });
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = namesRow2, EndRowIndex = namesRow2 + 1, StartColumnIndex = 5, EndColumnIndex = 7 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = false } } }, Fields = "userEnteredFormat.textFormat.bold" } });

            // Format: Bold Positions
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = posRow1, EndRowIndex = posRow1 + 1, StartColumnIndex = 3, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } }, Fields = "userEnteredFormat.textFormat.bold" } });
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = posRow2, EndRowIndex = posRow2 + 1, StartColumnIndex = 5, EndColumnIndex = 7 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } }, Fields = "userEnteredFormat.textFormat.bold" } });
        }


        // AI METHOD FOR CHECKER
        // AI METHOD FOR CHECKER
        public async Task<IActionResult> OnGetExplainAsync()
        {
            // 1. Fetch UI report data
            await LoadMonthAsync();

            var firstDay = new DateTime(int.Parse(SelectedMonth.Split('-')[0]), int.Parse(SelectedMonth.Split('-')[1]), 1);
            var lastDay = firstDay.AddMonths(1).AddDays(-1);
            string firstDayStr = firstDay.ToString("yyyy-MM-dd");
            string lastDayStr = lastDay.ToString("yyyy-MM-dd");
            var previousMonthStr = firstDay.AddMonths(-1).ToString("yyyy-MM");

            // A. Beginning Balances
            var prevBalancesResponse = await _supabase.Client.From<MonthlyFundBalance>()
                .Filter("report_month", Supabase.Postgrest.Constants.Operator.Equals, previousMonthStr).Get();
            var beginningBalances = prevBalancesResponse.Models.ToList();

            decimal begGen = beginningBalances.FirstOrDefault(b => b.FundName == "General")?.EndingBalance ?? 0;
            decimal begPledges = beginningBalances.FirstOrDefault(b => b.FundName == "Pledges")?.EndingBalance ?? 0;
            decimal begConst = beginningBalances.FirstOrDefault(b => b.FundName == "Construction")?.EndingBalance ?? 0;
            decimal begPW = beginningBalances.FirstOrDefault(b => b.FundName == "Praise & Worship")?.EndingBalance ?? 0;

            // B. Bulk Fetch Month Income Data
            var givingResp = await _supabase.Client.From<GivingRecord>()
                .Filter("service_date", Supabase.Postgrest.Constants.Operator.GreaterThanOrEqual, firstDayStr)
                .Filter("service_date", Supabase.Postgrest.Constants.Operator.LessThanOrEqual, lastDayStr).Get();
            var givingRecords = givingResp.Models.ToList();
            var givingRecordIds = givingRecords.Select(g => (object)g.Id).ToList();

            var monthGivingEntries = new List<GivingEntry>();
            if (givingRecordIds.Any())
            {
                var entryResp = await _supabase.Client.From<GivingEntry>().Filter("giving_record_id", Supabase.Postgrest.Constants.Operator.In, givingRecordIds).Get();
                monthGivingEntries = entryResp.Models.ToList();
            }

            // C. Bulk Fetch Month Disbursement Data
            var disbResp = await _supabase.Client.From<DisbursementRecord>()
                .Filter("record_date", Supabase.Postgrest.Constants.Operator.GreaterThanOrEqual, firstDayStr)
                .Filter("record_date", Supabase.Postgrest.Constants.Operator.LessThanOrEqual, lastDayStr).Get();
            var disbRecords = disbResp.Models.ToList();
            var disbRecordIds = disbRecords.Select(d => (object)d.Id).ToList();

            var allVouchers = new List<Voucher>();
            if (disbRecordIds.Any())
            {
                var vResp = await _supabase.Client.From<Voucher>().Filter("disbursement_record_id", Supabase.Postgrest.Constants.Operator.In, disbRecordIds).Get();
                allVouchers = vResp.Models.ToList();
            }

            var voucherIds = allVouchers.Select(v => (object)v.Id).ToList();
            var allVoucherItems = new List<VoucherItem>();
            if (voucherIds.Any())
            {
                var viResp = await _supabase.Client.From<VoucherItem>().Filter("voucher_id", Supabase.Postgrest.Constants.Operator.In, voucherIds).Get();
                allVoucherItems = viResp.Models.ToList();
            }

            var ledger = new VerificationLedger { ReportMonth = MonthReport.MonthLabel };
            decimal totalMonthGiving = 0;
            decimal totalMonthAdjDisb = 0;

            // ====================================================================
            // DYNAMIC DAILY LOOP
            // ====================================================================
            foreach (var page in MonthReport.Pages.Where(p => p.HasReport))
            {
                string pageDateStr = page.ReportDate.ToString("yyyy-MM-dd");

                // 1. Get accurate daily income breakdown from GivingEntries
                var dayRecordIds = givingRecords.Where(r => r.ServiceDate.ToString("yyyy-MM-dd") == pageDateStr).Select(r => r.Id).ToList();
                var dayEntries = monthGivingEntries.Where(e => dayRecordIds.Contains(e.GivingRecordId)).ToList();

                decimal dayGen = dayEntries.Sum(e => e.Tithes + e.Offerings) + dayEntries.Where(e => e.OthersFund == "General" || string.IsNullOrWhiteSpace(e.OthersFund)).Sum(e => e.Others);
                decimal dayPledges = dayEntries.Sum(e => e.Solomon + e.Noah + e.Mission) + dayEntries.Where(e => e.OthersFund == "Pledges").Sum(e => e.Others);
                decimal dayConst = dayEntries.Where(e => e.OthersFund == "Construction").Sum(e => e.Others);
                decimal dayPW = dayEntries.Where(e => e.OthersFund == "Praise & Worship").Sum(e => e.Others);

                // 2. Get accurate daily disbursement breakdown from VoucherItems
                var dailyVoucherItems = allVoucherItems.Where(i =>
                    allVouchers.Any(v => v.Id == i.VoucherId &&
                    disbRecords.Any(dr => dr.Id == v.DisbursementRecordId && dr.RecordDate.ToString("yyyy-MM-dd") == pageDateStr))).ToList();

                decimal dDisbGen = dailyVoucherItems.Where(i => i.FundSource == "General" || string.IsNullOrWhiteSpace(i.FundSource)).Sum(i => i.Amount - i.AmountReturned);
                decimal dDisbPledges = dailyVoucherItems.Where(i => i.FundSource == "Pledges").Sum(i => i.Amount - i.AmountReturned);
                decimal dDisbConst = dailyVoucherItems.Where(i => i.FundSource == "Construction").Sum(i => i.Amount - i.AmountReturned);
                decimal dDisbPW = dailyVoucherItems.Where(i => i.FundSource == "Praise & Worship").Sum(i => i.Amount - i.AmountReturned);

                var dailySummary = new DailyTransactionSummary
                {
                    Date = page.ReportDate.ToString("MMMM dd, yyyy"),
                    DateId = page.ReportDate.ToString("yyyy-MM-dd"),
                    TotalBlessing = page.Report.Giving.GrandTotal,
                    TotalDisbursement = page.Report.Disbursement.TotalReleased,
                    TotalCashReturn = page.Report.Disbursement.TotalReturned,
                    AdjustedDisbursement = page.Report.Disbursement.TotalNetDisbursement,
                    BlessingByWallet = new Dictionary<string, decimal>(),
                    DisbursementByWallet = new Dictionary<string, decimal>()
                };

                // Populate Blessing Dictionary (Only if > 0)
                if (dayGen > 0) dailySummary.BlessingByWallet.Add("General Fund", dayGen);
                if (dayPledges > 0) dailySummary.BlessingByWallet.Add("Pledges", dayPledges);
                if (dayConst > 0) dailySummary.BlessingByWallet.Add("Construction", dayConst);
                if (dayPW > 0) dailySummary.BlessingByWallet.Add("Praise & Worship", dayPW);

                // Populate Disbursement Dictionary (Only if > 0)
                if (dDisbGen > 0) dailySummary.DisbursementByWallet.Add("General Fund", dDisbGen);
                if (dDisbPledges > 0) dailySummary.DisbursementByWallet.Add("Pledges", dDisbPledges);
                if (dDisbConst > 0) dailySummary.DisbursementByWallet.Add("Construction", dDisbConst);
                if (dDisbPW > 0) dailySummary.DisbursementByWallet.Add("Praise & Worship", dDisbPW);

                ledger.DailySummaries.Add(dailySummary);

                totalMonthGiving += page.Report.Giving.GrandTotal;
                totalMonthAdjDisb += page.Report.Disbursement.TotalNetDisbursement;
            }

            // 4. Overall & Wallet Health (Monthly Totals)
            decimal incPledges = monthGivingEntries.Sum(e => e.Solomon + e.Noah + e.Mission + (e.OthersFund == "Pledges" ? e.Others : 0));
            decimal incGen = monthGivingEntries.Sum(e => e.Tithes + e.Offerings + (e.OthersFund == "General" || string.IsNullOrWhiteSpace(e.OthersFund) ? e.Others : 0));
            decimal incConst = monthGivingEntries.Where(e => e.OthersFund == "Construction").Sum(e => e.Others);
            decimal incPW = monthGivingEntries.Where(e => e.OthersFund == "Praise & Worship").Sum(e => e.Others);

            decimal disbGen = allVoucherItems.Where(i => i.FundSource == "General" || string.IsNullOrWhiteSpace(i.FundSource)).Sum(i => i.Amount - i.AmountReturned);
            decimal disbPledges = allVoucherItems.Where(i => i.FundSource == "Pledges").Sum(i => i.Amount - i.AmountReturned);
            decimal disbConst = allVoucherItems.Where(i => i.FundSource == "Construction").Sum(i => i.Amount - i.AmountReturned);
            decimal disbPW = allVoucherItems.Where(i => i.FundSource == "Praise & Worship").Sum(i => i.Amount - i.AmountReturned);

            decimal overallBegBal = beginningBalances.Sum(b => b.EndingBalance);
            ledger.Overall = new OverallSummary { BeginningBalance = overallBegBal, TotalGiving = totalMonthGiving, TotalAdjustedDisbursement = totalMonthAdjDisb, NetCashBalance = overallBegBal + totalMonthGiving - totalMonthAdjDisb };

            ledger.FundAudits = new Dictionary<string, FundAudit>
            {
                { "General Fund", new FundAudit { BeginningBalance = begGen, TotalIncome = incGen, TotalDisbursements = disbGen, CalculatedEndingBalance = begGen + incGen - disbGen, IsMathBalanced = true, IsInDeficit = (begGen + incGen - disbGen) < 0 } },
                { "Pledges", new FundAudit { BeginningBalance = begPledges, TotalIncome = incPledges, TotalDisbursements = disbPledges, CalculatedEndingBalance = begPledges + incPledges - disbPledges, IsMathBalanced = true, IsInDeficit = (begPledges + incPledges - disbPledges) < 0 } },
                { "Construction", new FundAudit { BeginningBalance = begConst, TotalIncome = incConst, TotalDisbursements = disbConst, CalculatedEndingBalance = begConst + incConst - disbConst, IsMathBalanced = true, IsInDeficit = (begConst + incConst - disbConst) < 0 } },
                { "Praise & Worship", new FundAudit { BeginningBalance = begPW, TotalIncome = incPW, TotalDisbursements = disbPW, CalculatedEndingBalance = begPW + incPW - disbPW, IsMathBalanced = true, IsInDeficit = (begPW + incPW - disbPW) < 0 } }
            };

            ledger.SystemDashboard = new DashboardTotals { BookBalance = ledger.Overall.NetCashBalance, CashOnHand = ledger.Overall.NetCashBalance };

            // ====================================================================
            // 🚨 THE NEW AI CACHING LOGIC 🚨
            // ====================================================================

            // 1. Generate the Fingerprint (Combines BegBal + Receipts - Disb)
            string currentFingerprint = $"{ledger.Overall.BeginningBalance}_{ledger.Overall.TotalGiving}_{ledger.Overall.TotalAdjustedDisbursement}";

            // 2. Check the Database to see if a report for this MONTH exists
            var cachedLogQuery = await _supabase.Client.From<AiAuditLog>()
                .Filter("report_month", Supabase.Postgrest.Constants.Operator.Equals, SelectedMonth)
                .Get();

            var existingLog = cachedLogQuery.Models.FirstOrDefault();

            if (existingLog != null && existingLog.DataFingerprint == currentFingerprint)
            {
                // CACHE HIT (0.1 SECONDS): The data hasn't changed. Load the saved HTML!
                AiSummary = existingLog.AiHtmlSummary;
            }
            else
            {
                // CACHE MISS (8 SECONDS): The data is new or changed. Ask Gemini to write a fresh report.
                AiSummary = await _aiAuditor.GenerateAuditSummaryAsync(ledger);

                if (existingLog != null)
                {
                    // UPDATE: The month already exists, but the fingerprint changed (Treasurer made an edit)
                    // We just overwrite the old AI summary with the new one.
                    existingLog.DataFingerprint = currentFingerprint;
                    existingLog.AiHtmlSummary = AiSummary;
                    existingLog.CreatedAt = DateTime.UtcNow;

                    await _supabase.Client.From<AiAuditLog>().Update(existingLog);
                }
                else
                {
                    // INSERT: First time anyone has ever generated a report for this month
                    var newLog = new AiAuditLog
                    {
                        Id = Guid.NewGuid().ToString(), // 👈 THIS FIXES THE CRASH! Generates a unique ID in C#
                        ReportMonth = SelectedMonth,
                        DataFingerprint = currentFingerprint,
                        AiHtmlSummary = AiSummary,
                        CreatedAt = DateTime.UtcNow
                    };

                    await _supabase.Client.From<AiAuditLog>().Insert(newLog);
                }
            }

            return Page();
        }

        public async Task<IActionResult> OnPostSaveSettingsAsync()
        {
            if (!User.IsInRole("admin") && !User.IsInRole("Admin"))
                return RedirectToPage("/Account/AccessDenied");

            await _supabase.InitializeAsync(true);

            AppSettings.Id = 1;
            await _supabase.Client.From<SystemSettingsRecord>().Upsert(AppSettings);

            Message = "Google Sheet IDs updated successfully!";
            return RedirectToPage(new { SelectedMonth });
        }
    }

    public class LedgerTransaction
    {
        public DateTime Date { get; set; }
        public string Particulars { get; set; } = string.Empty;
        public decimal ReceiptAmount { get; set; }
        public decimal DisbursementAmount { get; set; }

        public decimal GeneralFund { get; set; }
        public decimal CE { get; set; }
        public decimal Mission { get; set; }
        public decimal Solomon { get; set; }
        public decimal Noah { get; set; }
        public decimal PW { get; set; }
        public decimal Ceiling { get; set; }
        public decimal Pledge { get; set; }

        public string Ministry { get; set; } = string.Empty;
        public string FundSource { get; set; } = "General";
    }
}