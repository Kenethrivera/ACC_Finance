using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Supabase.Postgrest;
using static Supabase.Postgrest.Constants;
using acc_finance.Models;
using acc_finance.Models.Reports;
using acc_finance.Services;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using System.Text.RegularExpressions;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System;

namespace acc_finance.Pages.Reports
{
    public class MonthlyModel : PageModel
    {
        private readonly SupabaseService _supabase;

        public MonthlyModel(SupabaseService supabase)
        {
            _supabase = supabase;
        }

        [BindProperty(SupportsGet = true)]
        public string SelectedMonth { get; set; } = "";

        public MonthlyFinancialReportVm MonthReport { get; set; } = new();

        public string Message { get; set; } = "";

        public async Task OnGetAsync()
        {
            await LoadMonthAsync();
        }

        public async Task<IActionResult> OnGetExportAsync()
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

            string credentialPath = "google-credentials.json";
            string spreadsheetId = "11zTb5fVncgnsm1lOs6eilf51h0RhROguEq_HDPttHh4";

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

            var tabName = MonthReport.MonthLabel;
            var spreadsheet = await service.Spreadsheets.Get(spreadsheetId).ExecuteAsync();
            var existingSheet = spreadsheet.Sheets.FirstOrDefault(s => s.Properties.Title == tabName);

            if (existingSheet != null)
            {
                int oldSheetId = existingSheet.Properties.SheetId ?? 0;
                var deleteRequest = new Request { DeleteSheet = new DeleteSheetRequest { SheetId = oldSheetId } };
                await service.Spreadsheets.BatchUpdate(new BatchUpdateSpreadsheetRequest { Requests = new List<Request> { deleteRequest } }, spreadsheetId).ExecuteAsync();
            }

            var addSheetRequest = new Request { AddSheet = new AddSheetRequest { Properties = new SheetProperties { Title = tabName } } };
            var response = await service.Spreadsheets.BatchUpdate(new BatchUpdateSpreadsheetRequest { Requests = new List<Request> { addSheetRequest } }, spreadsheetId).ExecuteAsync();

            int sheetId = response.Replies[0].AddSheet.Properties.SheetId ?? 0;

            List<List<object>> grid = new List<List<object>>();
            List<Request> formatRequests = new List<Request>();
            int currentRow = 0;

            foreach (var page in pagesWithRecords)
            {
                int maxRowUsed = BuildGoogleSheetData(grid, formatRequests, sheetId, currentRow, page.Report);
                currentRow = maxRowUsed + 3;
            }

            IList<IList<object>> allRows = grid.Select(r => (IList<object>)r).ToList();

            var valueRange = new ValueRange { Values = allRows };
            var updateRequest = service.Spreadsheets.Values.Update(valueRange, spreadsheetId, $"{tabName}!A1");
            updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
            await updateRequest.ExecuteAsync();

            if (formatRequests.Any())
            {
                formatRequests.Add(new Request
                {
                    RepeatCell = new RepeatCellRequest
                    {
                        Range = new GridRange { SheetId = sheetId },
                        Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { FontFamily = "Calibri", FontSize = 11 } } },
                        Fields = "userEnteredFormat.textFormat(fontFamily,fontSize)"
                    }
                });

                var formatBatch = new BatchUpdateSpreadsheetRequest { Requests = formatRequests };
                await service.Spreadsheets.BatchUpdate(formatBatch, spreadsheetId).ExecuteAsync();
            }

            var resizeAuto1 = new Request
            {
                AutoResizeDimensions = new AutoResizeDimensionsRequest
                {
                    Dimensions = new DimensionRange { SheetId = sheetId, Dimension = "COLUMNS", StartIndex = 1, EndIndex = 12 }
                }
            };

            var resizeAuto2 = new Request
            {
                AutoResizeDimensions = new AutoResizeDimensionsRequest
                {
                    Dimensions = new DimensionRange { SheetId = sheetId, Dimension = "COLUMNS", StartIndex = 13, EndIndex = 16 }
                }
            };

            var resizeAuto3 = new Request
            {
                AutoResizeDimensions = new AutoResizeDimensionsRequest
                {
                    Dimensions = new DimensionRange { SheetId = sheetId, Dimension = "COLUMNS", StartIndex = 18, EndIndex = 25 }
                }
            };

            var resizeManualM = new Request
            {
                UpdateDimensionProperties = new UpdateDimensionPropertiesRequest
                {
                    Range = new DimensionRange { SheetId = sheetId, Dimension = "COLUMNS", StartIndex = 12, EndIndex = 13 },
                    Properties = new DimensionProperties { PixelSize = 80 },
                    Fields = "pixelSize"
                }
            };

            var resizeManualQ = new Request
            {
                UpdateDimensionProperties = new UpdateDimensionPropertiesRequest
                {
                    Range = new DimensionRange { SheetId = sheetId, Dimension = "COLUMNS", StartIndex = 16, EndIndex = 17 },
                    Properties = new DimensionProperties { PixelSize = 95 },
                    Fields = "pixelSize"
                }
            };

            var resizeManualR = new Request
            {
                UpdateDimensionProperties = new UpdateDimensionPropertiesRequest
                {
                    Range = new DimensionRange { SheetId = sheetId, Dimension = "COLUMNS", StartIndex = 17, EndIndex = 18 },
                    Properties = new DimensionProperties { PixelSize = 95 },
                    Fields = "pixelSize"
                }
            };

            await service.Spreadsheets.BatchUpdate(new BatchUpdateSpreadsheetRequest { Requests = new List<Request> { resizeAuto1, resizeAuto2, resizeAuto3, resizeManualM, resizeManualQ, resizeManualR } }, spreadsheetId).ExecuteAsync();

            Message = $"Successfully exported to Google Sheets: {tabName} tab updated!";
            return Page();
        }

        private async Task LoadMonthAsync()
        {
            await _supabase.InitializeAsync(true);

            if (string.IsNullOrWhiteSpace(SelectedMonth))
                SelectedMonth = DateTime.Today.ToString("yyyy-MM");

            if (!DateTime.TryParse($"{SelectedMonth}-01", out var monthStart))
            {
                monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                SelectedMonth = monthStart.ToString("yyyy-MM");
            }

            var firstDay = new DateTime(monthStart.Year, monthStart.Month, 1);
            var lastDay = firstDay.AddMonths(1).AddDays(-1);

            MonthReport = new MonthlyFinancialReportVm
            {
                SelectedMonth = SelectedMonth,
                MonthLabel = firstDay.ToString("MMMM yyyy"),
                Pages = new List<MonthlyReportPageVm>()
            };

            var memberResponse = await _supabase.Client.From<Member>().Get();
            var allMembers = memberResponse.Models.ToList();

            var voucherItemResponse = await _supabase.Client.From<VoucherItem>().Get();
            var allVoucherItems = voucherItemResponse.Models.ToList();

            var sundays = GetAllSundaysInMonth(firstDay, lastDay);

            foreach (var sunday in sundays)
            {
                var page = await BuildMonthlyPageAsync(sunday, allMembers, allVoucherItems);
                MonthReport.Pages.Add(page);
            }
        }

        private async Task<MonthlyReportPageVm> BuildMonthlyPageAsync(DateTime reportDate, List<Member> members, List<VoucherItem> allVoucherItems)
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

            var givingRecordResponse = await _supabase.Client
                .From<GivingRecord>()
                .Filter("service_date", Constants.Operator.Equals, dateOnly)
                .Get();

            var givingRecord = givingRecordResponse.Models.FirstOrDefault();

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

            var givingEntryResponse = await _supabase.Client
                .From<GivingEntry>()
                .Filter("giving_record_id", Constants.Operator.Equals, givingRecord.Id.ToString())
                .Get();

            var givingEntries = givingEntryResponse.Models.ToList();

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

            var denominationResponse = await _supabase.Client
                .From<GivingDenomination>()
                .Filter("giving_record_id", Constants.Operator.Equals, givingRecord.Id.ToString())
                .Get();

            var denomination = denominationResponse.Models.FirstOrDefault();

            if (denomination != null)
            {
                page.Report.Denomination.Exists = true;
                page.Report.Denomination.Lines = BuildDenominationLines(denomination);
                page.Report.Denomination.Total = denomination.Total;
            }

            var disbursementRecordResponse = await _supabase.Client
                .From<DisbursementRecord>()
                .Filter("giving_record_id", Constants.Operator.Equals, givingRecord.Id.ToString())
                .Get();

            var disbursementRecord = disbursementRecordResponse.Models.FirstOrDefault();

            if (disbursementRecord != null)
            {
                page.Report.Disbursement.TotalReleased = disbursementRecord.TotalReleased;
                page.Report.Disbursement.TotalReturned = disbursementRecord.TotalReturned;
                page.Report.Disbursement.TotalNetDisbursement = disbursementRecord.TotalReleased - disbursementRecord.TotalReturned;

                var voucherResponse = await _supabase.Client
                    .From<Voucher>()
                    .Filter("disbursement_record_id", Constants.Operator.Equals, disbursementRecord.Id.ToString())
                    .Get();

                var vouchers = voucherResponse.Models.ToList();

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

        private void SetCell(List<List<object>> grid, int row, int col, object value)
        {
            while (grid.Count <= row) grid.Add(new List<object>());
            while (grid[row].Count <= col) grid[row].Add("");
            grid[row][col] = value ?? "";
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
            string rawName = report.Giving.RecordCode ?? "CR_00";
            string safeTableName = Regex.Replace(rawName, "[^a-zA-Z0-9_]", "_");
            if (char.IsDigit(safeTableName[0])) safeTableName = "T_" + safeTableName;

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
                SetCell(grid, row, col + 7, item.Total > 0 ? item.Total : "");
                row++;
            }

            SetCell(grid, row, col, "TOTAL");
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
                        Range = new GridRange
                        {
                            SheetId = sheetId,
                            StartRowIndex = givingStartRow,
                            EndRowIndex = row,
                            StartColumnIndex = col,
                            EndColumnIndex = col + 8
                        }
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
                        Range = new GridRange
                        {
                            SheetId = sheetId,
                            StartRowIndex = denomStartRow,
                            EndRowIndex = row,
                            StartColumnIndex = col,
                            EndColumnIndex = col + 5
                        }
                    }
                }
            });

            FormatTableTotalsRow(formatRequests, sheetId, row - 1, col, col + 5);
            AddNumberFormatting(formatRequests, sheetId, denomStartRow + 1, row, col, col + 1, "#,##0");
            AddNumberFormatting(formatRequests, sheetId, denomStartRow + 1, row, col + 3, col + 5);

            formatRequests.Add(new Request
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = new GridRange { SheetId = sheetId, StartRowIndex = denomStartRow + 1, EndRowIndex = row - 1, StartColumnIndex = col, EndColumnIndex = col + 1 },
                    Cell = new CellData { UserEnteredFormat = new CellFormat { HorizontalAlignment = "RIGHT" } },
                    Fields = "userEnteredFormat.horizontalAlignment"
                }
            });

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

            formatRequests.Add(new Request
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = new GridRange { SheetId = sheetId, StartRowIndex = disbStartRow, EndRowIndex = disbStartRow + 1, StartColumnIndex = col, EndColumnIndex = col + 6 },
                    Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } },
                    Fields = "userEnteredFormat.textFormat(bold,fontFamily)"
                }
            });

            formatRequests.Add(new Request
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = new GridRange { SheetId = sheetId, StartRowIndex = disbStartRow, EndRowIndex = disbStartRow + 1, StartColumnIndex = col + 5, EndColumnIndex = col + 6 },
                    Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { ForegroundColor = new Color { Red = 1f, Green = 0f, Blue = 0f } } } },
                    Fields = "userEnteredFormat.textFormat(foregroundColor,fontFamily)"
                }
            });

            formatRequests.Add(new Request
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = new GridRange { SheetId = sheetId, StartRowIndex = disbStartRow, EndRowIndex = disbStartRow + 1, StartColumnIndex = col + 2, EndColumnIndex = col + 6 },
                    Cell = new CellData { UserEnteredFormat = new CellFormat { HorizontalAlignment = "RIGHT", TextFormat = new TextFormat() } },
                    Fields = "userEnteredFormat(horizontalAlignment,textFormat.fontFamily)"
                }
            });

            formatRequests.Add(new Request
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = new GridRange { SheetId = sheetId, StartRowIndex = disbStartRow + 1, EndRowIndex = row, StartColumnIndex = col + 4, EndColumnIndex = col + 5 },
                    Cell = new CellData { UserEnteredFormat = new CellFormat { HorizontalAlignment = "RIGHT", TextFormat = new TextFormat { FontFamily = "Calibri" } } },
                    Fields = "userEnteredFormat(horizontalAlignment,textFormat.fontFamily)"
                }
            });

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

            formatRequests.Add(new Request
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = new GridRange { SheetId = sheetId, StartRowIndex = disbStartRow + 1, EndRowIndex = row, StartColumnIndex = col + 5, EndColumnIndex = col + 6 },
                    Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { FontFamily = "Calibri", ForegroundColor = new Color { Red = 1f, Green = 0f, Blue = 0f } } } },
                    Fields = "userEnteredFormat.textFormat(foregroundColor,fontFamily)"
                }
            });

            row++;

            int summaryBlockStart = row;

            SetCell(grid, row, col, "Adjusted disbursement");
            SetCell(grid, row, col + 2, report.Disbursement.TotalNetDisbursement);
            row += 2;

            SetCell(grid, row, col, "Cash Receipts / Blessings");
            SetCell(grid, row, col + 2, report.Summary.CashReceiptsOrBlessings);
            row++;

            int lessRow = row;
            SetCell(grid, row, col, "Less: Cash disbursements");
            SetCell(grid, row, col + 2, report.Summary.LessCashDisbursements);
            row++;

            formatRequests.Add(new Request
            {
                UpdateBorders = new UpdateBordersRequest
                {
                    Range = new GridRange { SheetId = sheetId, StartRowIndex = lessRow, EndRowIndex = lessRow + 1, StartColumnIndex = col + 2, EndColumnIndex = col + 3 },
                    Bottom = new Border { Style = "SOLID", Color = new Color { Red = 0f, Green = 0f, Blue = 0f } }
                }
            });

            SetCell(grid, row, col, $"Net Cash Balance {report.ReportDate:MM/dd/yyyy}");
            SetCell(grid, row, col + 2, report.Summary.NetCashBalance);
            row++;

            AddNumberFormatting(formatRequests, sheetId, summaryBlockStart, row, col + 2, col + 3);

            bool isPositive = report.Summary.NetCashBalance >= 0;
            var bgColor = isPositive ? new Color { Red = 0.88f, Green = 0.93f, Blue = 0.85f } : new Color { Red = 0.95f, Green = 0.8f, Blue = 0.8f };
            var textColor = isPositive ? new Color { Red = 0f, Green = 0f, Blue = 0f } : new Color { Red = 1f, Green = 0f, Blue = 0f };

            formatRequests.Add(new Request
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = new GridRange { SheetId = sheetId, StartRowIndex = row - 1, EndRowIndex = row, StartColumnIndex = col + 2, EndColumnIndex = col + 3 },
                    Cell = new CellData
                    {
                        UserEnteredFormat = new CellFormat
                        {
                            BackgroundColor = bgColor,
                            TextFormat = new TextFormat { Bold = true, FontFamily = "Calibri", ForegroundColor = textColor },
                            NumberFormat = new NumberFormat { Type = "NUMBER", Pattern = "#,##0.00;(#,##0.00)" }
                        }
                    },
                    Fields = "userEnteredFormat(backgroundColor,textFormat(bold,fontFamily,foregroundColor),numberFormat)"
                }
            });

            formatRequests.Add(new Request
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = new GridRange { SheetId = sheetId, StartRowIndex = summaryBlockStart, EndRowIndex = summaryBlockStart + 1, StartColumnIndex = col, EndColumnIndex = col + 3 },
                    Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } },
                    Fields = "userEnteredFormat.textFormat(bold,fontFamily)"
                }
            });

            formatRequests.Add(new Request
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = new GridRange { SheetId = sheetId, StartRowIndex = row - 1, EndRowIndex = row, StartColumnIndex = col, EndColumnIndex = col + 1 },
                    Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } },
                    Fields = "userEnteredFormat.textFormat(bold,fontFamily)"
                }
            });

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
                    Range = new GridRange { SheetId = sheetId, StartRowIndex = sumStartRow, EndRowIndex = sumStartRow + 1, StartColumnIndex = col + 2, EndColumnIndex = col + 4 },
                    Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true, FontFamily = "Calibri" } } },
                    Fields = "userEnteredFormat.textFormat(bold,fontFamily)"
                }
            });

            formatRequests.Add(new Request
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = new GridRange { SheetId = sheetId, StartRowIndex = totalRow, EndRowIndex = totalRow + 1, StartColumnIndex = col, EndColumnIndex = col + 4 },
                    Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true, FontFamily = "Calibri" } } },
                    Fields = "userEnteredFormat.textFormat(bold,fontFamily)"
                }
            });

            formatRequests.Add(new Request
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = new GridRange { SheetId = sheetId, StartRowIndex = sumStartRow, EndRowIndex = sumStartRow + 1, StartColumnIndex = col + 2, EndColumnIndex = col + 4 },
                    Cell = new CellData { UserEnteredFormat = new CellFormat { HorizontalAlignment = "RIGHT", TextFormat = new TextFormat { FontFamily = "Calibri" } } },
                    Fields = "userEnteredFormat(horizontalAlignment,textFormat.fontFamily)"
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
                    Fields = "userEnteredFormat(numberFormat,textFormat.fontFamily)"
                }
            });
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

            var denomValue = parts[0];
            var denomType = string.Join(" ", parts.Skip(1));

            return (denomValue, denomType);
        }

        // FINANCIAL REPORT EXPORT
        public async Task<IActionResult> OnGetExportFinancialAsync()
        {
            await _supabase.InitializeAsync(true);

            if (string.IsNullOrWhiteSpace(SelectedMonth)) SelectedMonth = DateTime.Today.ToString("yyyy-MM");
            if (!DateTime.TryParse($"{SelectedMonth}-01", out var monthStart)) monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);

            var firstDay = new DateTime(monthStart.Year, monthStart.Month, 1);
            var lastDay = firstDay.AddMonths(1).AddDays(-1);

            // 1. Get Beginning Balances (Previous Month)
            var previousMonthStr = firstDay.AddMonths(-1).ToString("yyyy-MM");
            var prevBalancesResponse = await _supabase.Client.From<MonthlyFundBalance>().Filter("report_month", Constants.Operator.Equals, previousMonthStr).Get();
            var beginningBalances = prevBalancesResponse.Models.ToList();


            // 2. Fetch all required data for the month
            var givingResponse = await _supabase.Client.From<GivingRecord>()
                .Filter("service_date", Constants.Operator.GreaterThanOrEqual, firstDay.ToString("yyyy-MM-dd"))
                .Filter("service_date", Constants.Operator.LessThanOrEqual, lastDay.ToString("yyyy-MM-dd")).Get();
            var givingRecords = givingResponse.Models;

            var givingEntryResponse = await _supabase.Client.From<GivingEntry>().Get();
            var allGivingEntries = givingEntryResponse.Models;

            var disbResponse = await _supabase.Client.From<DisbursementRecord>()
                .Filter("record_date", Constants.Operator.GreaterThanOrEqual, firstDay.ToString("yyyy-MM-dd"))
                .Filter("record_date", Constants.Operator.LessThanOrEqual, lastDay.ToString("yyyy-MM-dd")).Get();
            var disbRecords = disbResponse.Models;

            var voucherResponse = await _supabase.Client.From<Voucher>().Get();
            var allVouchers = voucherResponse.Models;

            var voucherItemResponse = await _supabase.Client.From<VoucherItem>().Get();
            var allVoucherItems = voucherItemResponse.Models;

            var transactions = new List<LedgerTransaction>();

            // Add Income
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

            // Add Disbursements
            foreach (var dr in disbRecords)
            {
                var vouchers = allVouchers.Where(v => v.DisbursementRecordId == dr.Id).ToList();
                foreach (var v in vouchers)
                {
                    var items = allVoucherItems.Where(i => i.VoucherId == v.Id).ToList();
                    var netAmount = items.Sum(i => i.Amount - i.AmountReturned);

                    var t = new LedgerTransaction
                    {
                        Date = dr.RecordDate,
                        Particulars = $"{string.Join(", ", items.Select(i => i.Particular))} - {v.Payee}",
                        DisbursementAmount = netAmount,
                        Ministry = v.Ministry ?? "Uncategorized"
                    };

                    var min = v.Ministry?.ToLower() ?? "";
                    if (min.Contains("mission")) t.Mission = netAmount;
                    else if (min.Contains("ce") || min.Contains("sunday")) t.CE = netAmount;
                    else if (min.Contains("pw") || min.Contains("women")) t.PW = netAmount;
                    else if (min.Contains("solomon")) t.Solomon = netAmount;
                    else if (min.Contains("noah")) t.Noah = netAmount;
                    else if (min.Contains("ceiling")) t.Ceiling = netAmount;
                    else if (min.Contains("pledge")) t.Pledge = netAmount;
                    else t.GeneralFund = netAmount;

                    transactions.Add(t);
                }
            }

            transactions = transactions.OrderBy(t => t.Date).ToList();

            string credentialPath = "google-credentials.json";
            string spreadsheetId = "1Ln4Ia6EsZw-0Z64yBRERZdyrej9GKZ8gNRm3dY1a3qY";

            GoogleCredential credential;
            using (var stream = new FileStream(credentialPath, FileMode.Open, FileAccess.Read))
            {
#pragma warning disable CS0618
                credential = GoogleCredential.FromStream(stream).CreateScoped(SheetsService.Scope.Spreadsheets);
#pragma warning restore CS0618
            }

            var service = new SheetsService(new BaseClientService.Initializer() { HttpClientInitializer = credential, ApplicationName = "FinanceApp" });

            // Change tab name to be just MONTH YEAR as requested
            var tabName = firstDay.ToString("MMMM yyyy").ToUpper(); // e.g., "JANUARY 2026"

            var spreadsheet = await service.Spreadsheets.Get(spreadsheetId).ExecuteAsync();
            var existingSheet = spreadsheet.Sheets.FirstOrDefault(s => s.Properties.Title == tabName);

            int sheetId;

            if (existingSheet != null)
            {
                // 1. UPDATE EXISTING: Keep the tab, just get its ID
                sheetId = existingSheet.Properties.SheetId ?? 0;

                // 2. Clear all old text/numbers so shorter reports don't leave leftover data
                var clearRequest = service.Spreadsheets.Values.Clear(new ClearValuesRequest(), spreadsheetId, tabName);
                await clearRequest.ExecuteAsync();

                // 3. Clear old formatting (colors, borders) so we can apply fresh ones
                var unformatRequest = new Request
                {
                    UpdateCells = new UpdateCellsRequest
                    {
                        Range = new GridRange { SheetId = sheetId },
                        Fields = "userEnteredFormat"
                    }
                };
                await service.Spreadsheets.BatchUpdate(new BatchUpdateSpreadsheetRequest { Requests = new List<Request> { unformatRequest } }, spreadsheetId).ExecuteAsync();
            }
            else
            {
                // 1. CREATE NEW: Tab didn't exist, so add it
                var addSheetRequest = new Request { AddSheet = new AddSheetRequest { Properties = new SheetProperties { Title = tabName } } };
                var response = await service.Spreadsheets.BatchUpdate(new BatchUpdateSpreadsheetRequest { Requests = new List<Request> { addSheetRequest } }, spreadsheetId).ExecuteAsync();
                sheetId = response.Replies[0].AddSheet.Properties.SheetId ?? 0;
            }

            List<List<object>> grid = new List<List<object>>();
            List<Request> formatRequests = new List<Request>();

            // Filter the entries to only include those from the current month's giving records
            var monthGivingRecordIds = givingRecords.Select(r => r.Id.ToString()).ToList();
            var monthGivingEntries = allGivingEntries.Where(e => monthGivingRecordIds.Contains(e.GivingRecordId.ToString())).ToList();

            BuildFinancialReportGrid(grid, formatRequests, sheetId, firstDay, beginningBalances, transactions, monthGivingEntries);

            IList<IList<object>> allRows = grid.Select(r => (IList<object>)r).ToList();
            var valueRange = new ValueRange { Values = allRows };
            var updateRequest = service.Spreadsheets.Values.Update(valueRange, spreadsheetId, $"{tabName}!A1");
            updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
            await updateRequest.ExecuteAsync();

            if (formatRequests.Any())
            {
                formatRequests.Add(new Request
                {
                    RepeatCell = new RepeatCellRequest
                    {
                        Range = new GridRange { SheetId = sheetId },
                        Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { FontFamily = "Arial", FontSize = 10 } } },
                        Fields = "userEnteredFormat.textFormat(fontFamily,fontSize)"
                    }
                });
                await service.Spreadsheets.BatchUpdate(new BatchUpdateSpreadsheetRequest { Requests = formatRequests }, spreadsheetId).ExecuteAsync();
            }

            await LoadMonthAsync();

            Message = $"Successfully exported Financial Report: {tabName}!";
            return Page();
        }

        private void BuildFinancialReportGrid(List<List<object>> grid, List<Request> formats, int sheetId, DateTime month, IReadOnlyList<MonthlyFundBalance> beginningBalances, List<LedgerTransaction> transactions, List<GivingEntry> givingEntries)
        {
            // 1. Calculate the exact last day of the given month
            var lastDay = new DateTime(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month));

            // 2. Pre-calculate your totals
            decimal totalBegBalance = beginningBalances.Sum(b => b.EndingBalance);
            decimal totalReceipts = transactions.Sum(t => t.ReceiptAmount);
            decimal totalDisb = transactions.Sum(t => t.DisbursementAmount);

            
            // EXCEL LAYOUT: HEADERS & BEGINNING BALANCE
            
            int row = 1; // Row 2 in Excel
            SetCell(grid, row, 1, "ADELINA CHRISTIAN CHURCH");
            SetCell(grid, row + 1, 1, "Financial Report");
            SetCell(grid, row + 2, 1, lastDay.ToString("MMMM dd, yyyy"));

            row = 5; // Row 6
            SetCell(grid, row, 8, "AMOUNT");

            row = 6; // Row 7
            SetCell(grid, row, 1, "BEGINNING BALANCE");
            SetCell(grid, row, 8, totalBegBalance);

            row = 7; // Row 8
            SetCell(grid, row, 1, "ADD: CASH RECEIPTS / BLESSINGS");

            // Format Headers
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 1, EndRowIndex = 2, StartColumnIndex = 1, EndColumnIndex = 9 }, MergeType = "MERGE_ALL" } });
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 2, EndRowIndex = 3, StartColumnIndex = 1, EndColumnIndex = 9 }, MergeType = "MERGE_ALL" } });
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 3, EndRowIndex = 4, StartColumnIndex = 1, EndColumnIndex = 9 }, MergeType = "MERGE_ALL" } });
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 6, EndRowIndex = 7, StartColumnIndex = 1, EndColumnIndex = 4 }, MergeType = "MERGE_ALL" } });
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 7, EndRowIndex = 8, StartColumnIndex = 1, EndColumnIndex = 4 }, MergeType = "MERGE_ALL" } });

            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 1, EndRowIndex = 4, StartColumnIndex = 1, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { HorizontalAlignment = "CENTER" } }, Fields = "userEnteredFormat.horizontalAlignment" } });
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 1, EndRowIndex = 3, StartColumnIndex = 1, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } }, Fields = "userEnteredFormat.textFormat.bold" } });
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 5, EndRowIndex = 6, StartColumnIndex = 8, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true }, HorizontalAlignment = "RIGHT" } }, Fields = "userEnteredFormat(textFormat.bold,horizontalAlignment)" } });
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 6, EndRowIndex = 7, StartColumnIndex = 1, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } }, Fields = "userEnteredFormat.textFormat.bold" } });
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 6, EndRowIndex = 7, StartColumnIndex = 8, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { NumberFormat = new NumberFormat { Type = "CURRENCY", Pattern = "\"₱\"#,##0.00" } } }, Fields = "userEnteredFormat.numberFormat" } });
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 7, EndRowIndex = 8, StartColumnIndex = 1, EndColumnIndex = 4 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } }, Fields = "userEnteredFormat.textFormat.bold" } });

            // EXCEL LAYOUT: SOURCES OF BLESSINGS
            row = 8;
            SetCell(grid, row, 2, "Sources of Blessings:");
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = row, EndRowIndex = row + 1, StartColumnIndex = 2, EndColumnIndex = 4 }, MergeType = "MERGE_ALL" } });
            row++;

            decimal valTithes = givingEntries.Sum(e => e.Tithes);
            decimal valOfferings = givingEntries.Sum(e => e.Offerings);

            SetCell(grid, row, 3, "Tithes"); SetCell(grid, row, 8, valTithes); row++;
            SetCell(grid, row, 3, "Offerings"); SetCell(grid, row, 8, valOfferings); row++;
            SetCell(grid, row, 3, "Pledges:"); row++;

            decimal valSolomon = givingEntries.Sum(e => e.Solomon);
            SetCell(grid, row, 3, "    Solomon"); SetCell(grid, row, 7, valSolomon); row++;

            decimal valNoah = givingEntries.Sum(e => e.Noah);
            SetCell(grid, row, 3, "    Noah"); SetCell(grid, row, 7, valNoah); SetCell(grid, row, 8, valSolomon + valNoah);
            int noahRow = row; row++;

            decimal valMission = givingEntries.Sum(e => e.Mission);
            SetCell(grid, row, 3, "Mission"); SetCell(grid, row, 8, valMission); row++;

            SetCell(grid, row, 3, "Others"); row++;

            var othersList = givingEntries.Where(e => e.Others > 0).ToList();
            decimal othersTotal = 0;
            foreach (var item in othersList)
            {
                string label = !string.IsNullOrWhiteSpace(item.EntryName) ? item.EntryName : "(Unnamed Entry)";
                SetCell(grid, row, 3, "    " + label); SetCell(grid, row, 7, item.Others);
                othersTotal += item.Others;
                row++;
            }
            if (!othersList.Any()) { row++; }

            int lastOtherRow = row - 1;
            if (othersList.Any()) { SetCell(grid, lastOtherRow, 8, othersTotal); }

            int totalReceiptRow = row;
            SetCell(grid, totalReceiptRow, 1, "Total");
            SetCell(grid, totalReceiptRow, 8, totalBegBalance + totalReceipts);
            row++;

            // Borders and Formatting for Receipts
            formats.Add(new Request { UpdateBorders = new UpdateBordersRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = noahRow, EndRowIndex = noahRow + 1, StartColumnIndex = 7, EndColumnIndex = 8 }, Bottom = new Border { Style = "SOLID", Color = new Color { Red = 0f, Green = 0f, Blue = 0f } } } });
            if (othersList.Any())
            {
                formats.Add(new Request { UpdateBorders = new UpdateBordersRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = lastOtherRow, EndRowIndex = lastOtherRow + 1, StartColumnIndex = 7, EndColumnIndex = 8 }, Bottom = new Border { Style = "SOLID", Color = new Color { Red = 0f, Green = 0f, Blue = 0f } } } });
            }

            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = 8, EndRowIndex = totalReceiptRow, StartColumnIndex = 7, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { NumberFormat = new NumberFormat { Type = "NUMBER", Pattern = "#,##0.00" } } }, Fields = "userEnteredFormat.numberFormat" } });
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = totalReceiptRow, EndRowIndex = totalReceiptRow + 1, StartColumnIndex = 1, EndColumnIndex = 2 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } }, Fields = "userEnteredFormat.textFormat.bold" } });

            // Light green background, bold black text, currency format
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = totalReceiptRow, EndRowIndex = totalReceiptRow + 1, StartColumnIndex = 8, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { BackgroundColor = new Color { Red = 0.85f, Green = 0.92f, Blue = 0.83f }, TextFormat = new TextFormat { Bold = true, ForegroundColor = new Color { Red = 0f, Green = 0f, Blue = 0f } }, NumberFormat = new NumberFormat { Type = "CURRENCY", Pattern = "\"₱\"#,##0.00" } } }, Fields = "userEnteredFormat(backgroundColor,textFormat(bold,foregroundColor),numberFormat)" } });

            
            // EXCEL LAYOUT: LESS DISBURSEMENTS
            
            row += 2; // Add 2 empty rows below receipt total
            int disbStartRow = row;

            SetCell(grid, row, 1, "LESS: DISBURSEMENTS");
            formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = row, EndRowIndex = row + 1, StartColumnIndex = 1, EndColumnIndex = 4 }, MergeType = "MERGE_ALL" } });
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = row, EndRowIndex = row + 1, StartColumnIndex = 1, EndColumnIndex = 4 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } }, Fields = "userEnteredFormat.textFormat.bold" } });
            row++;

            // Group alphabetically by Ministry
            var disbTransactions = transactions
                .Where(t => t.DisbursementAmount > 0)
                .GroupBy(t => string.IsNullOrWhiteSpace(t.Ministry) ? "Uncategorized" : t.Ministry)
                .OrderBy(g => g.Key)
                .ToList();

            int firstDisbDataRow = row;

            foreach (var group in disbTransactions)
            {
                var items = group.ToList();

                // Ministry Name (Bolded)
                SetCell(grid, row, 2, $"{group.Key} Expenses:");
                formats.Add(new Request { MergeCells = new MergeCellsRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = row, EndRowIndex = row + 1, StartColumnIndex = 2, EndColumnIndex = 4 }, MergeType = "MERGE_ALL" } });
                formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = row, EndRowIndex = row + 1, StartColumnIndex = 2, EndColumnIndex = 4 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } }, Fields = "userEnteredFormat.textFormat.bold" } });
                row++;

                // Roll-up Stipends by person's name dynamically
                var stipends = items
                    .Where(x => x.Particulars.Contains("Stipend", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(x => x.Particulars.Trim())
                    .Select(g => new { Particulars = g.Key, DisbursementAmount = g.Sum(x => x.DisbursementAmount) })
                    .ToList();

                var others = items.Where(x => !x.Particulars.Contains("Stipend", StringComparison.OrdinalIgnoreCase)).ToList();

                // If only 1 total item for this ministry, jump straight to Col I
                if (stipends.Count == 0 && others.Count == 1)
                {
                    SetCell(grid, row, 3, "    " + others[0].Particulars);
                    SetCell(grid, row, 8, others[0].DisbursementAmount);
                    row++;
                }
                else if (stipends.Count == 1 && others.Count == 0)
                {
                    SetCell(grid, row, 3, "    " + stipends[0].Particulars);
                    SetCell(grid, row, 8, stipends[0].DisbursementAmount);
                    row++;
                }
                else
                {
                    int lastStipendRow = -1;
                    decimal stipendTotal = 0;

                    foreach (var st in stipends)
                    {
                        SetCell(grid, row, 3, "    " + st.Particulars);
                        SetCell(grid, row, 6, st.DisbursementAmount); // Col G
                        stipendTotal += st.DisbursementAmount;
                        lastStipendRow = row;
                        row++;
                    }

                    if (stipends.Any())
                    {
                        SetCell(grid, lastStipendRow, 7, stipendTotal); // Col H subtotal
                        formats.Add(new Request { UpdateBorders = new UpdateBordersRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = lastStipendRow, EndRowIndex = lastStipendRow + 1, StartColumnIndex = 6, EndColumnIndex = 7 }, Bottom = new Border { Style = "SOLID", Color = new Color { Red = 0f, Green = 0f, Blue = 0f } } } });
                    }

                    int lastItemRow = row - 1;

                    foreach (var ot in others)
                    {
                        SetCell(grid, row, 3, "    " + ot.Particulars);
                        SetCell(grid, row, 7, ot.DisbursementAmount); // Col H
                        lastItemRow = row;
                        row++;
                    }

                    // Group overall total to Col I
                    decimal groupTotal = stipends.Sum(s => s.DisbursementAmount) + others.Sum(o => o.DisbursementAmount);
                    SetCell(grid, lastItemRow, 8, groupTotal);

                    formats.Add(new Request { UpdateBorders = new UpdateBordersRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = lastItemRow, EndRowIndex = lastItemRow + 1, StartColumnIndex = 7, EndColumnIndex = 8 }, Bottom = new Border { Style = "SOLID", Color = new Color { Red = 0f, Green = 0f, Blue = 0f } } } });
                }
            }

            // --- TOTAL DISBURSEMENTS ROW ---
            int totalDisbRow = row;
            SetCell(grid, totalDisbRow, 1, "Total");
            SetCell(grid, totalDisbRow, 8, totalDisb);
            row++;

            // Format Disbursements Col I as Bold
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = firstDisbDataRow, EndRowIndex = totalDisbRow, StartColumnIndex = 8, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } }, Fields = "userEnteredFormat.textFormat.bold" } });

            // Number format G, H, I
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = firstDisbDataRow, EndRowIndex = totalDisbRow + 1, StartColumnIndex = 6, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { NumberFormat = new NumberFormat { Type = "NUMBER", Pattern = "#,##0.00" } } }, Fields = "userEnteredFormat.numberFormat" } });

            // Bold Total label
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = totalDisbRow, EndRowIndex = totalDisbRow + 1, StartColumnIndex = 1, EndColumnIndex = 2 }, Cell = new CellData { UserEnteredFormat = new CellFormat { TextFormat = new TextFormat { Bold = true } } }, Fields = "userEnteredFormat.textFormat.bold" } });

            // Light Red background with Black Bold Text
            formats.Add(new Request { RepeatCell = new RepeatCellRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = totalDisbRow, EndRowIndex = totalDisbRow + 1, StartColumnIndex = 8, EndColumnIndex = 9 }, Cell = new CellData { UserEnteredFormat = new CellFormat { BackgroundColor = new Color { Red = 0.98f, Green = 0.8f, Blue = 0.8f }, TextFormat = new TextFormat { Bold = true, ForegroundColor = new Color { Red = 0f, Green = 0f, Blue = 0f } } } }, Fields = "userEnteredFormat(backgroundColor,textFormat(bold,foregroundColor))" } });

            // Top Border to emphasize the math line (over-underline)
            formats.Add(new Request { UpdateBorders = new UpdateBordersRequest { Range = new GridRange { SheetId = sheetId, StartRowIndex = totalDisbRow, EndRowIndex = totalDisbRow + 1, StartColumnIndex = 8, EndColumnIndex = 9 }, Top = new Border { Style = "SOLID", Color = new Color { Red = 0f, Green = 0f, Blue = 0f } } } });
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

        // Add this line:
        public string Ministry { get; set; } = string.Empty;
    }
}