using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using acc_finance.Models;
using acc_finance.Services;
using System.Text.Json;
using static Supabase.Postgrest.Constants;

namespace acc_finance.Pages.Disbursements
{
    public class CreateModel : PageModel
    {
        private readonly SupabaseService _supabase;

        public CreateModel(SupabaseService supabase)
        {
            _supabase = supabase;
        }

        [BindProperty(SupportsGet = true)]
        public DateTime RecordDate { get; set; } = DateTime.Today;

        [BindProperty]
        public string SheetJson { get; set; } = "";

        public DisbursementSheetVm Sheet { get; set; } = new();
        public List<TemplatePayloadVm> FixedTemplates { get; set; } = new();

        public async Task OnGet()
        {
            await _supabase.InitializeAsync(true);
            Sheet = await BuildSheetAsync(RecordDate);
            FixedTemplates = await LoadTemplatesAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            await _supabase.InitializeAsync(true);

            if (RecordDate == DateTime.MinValue)
            {
                ModelState.AddModelError(string.Empty, "Please choose a valid date.");
                Sheet = new DisbursementSheetVm();
                return Page();
            }

            if (string.IsNullOrWhiteSpace(SheetJson))
            {
                ModelState.AddModelError(string.Empty, "No sheet data was submitted.");
                Sheet = await BuildSheetAsync(RecordDate);
                return Page();
            }

            DisbursementSheetPostVm? postedSheet;

            try
            {
                postedSheet = JsonSerializer.Deserialize<DisbursementSheetPostVm>(
                    SheetJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                );
            }
            catch
            {
                ModelState.AddModelError(string.Empty, "Failed to read the voucher sheet data.");
                Sheet = await BuildSheetAsync(RecordDate);
                return Page();
            }

            if (postedSheet == null)
            {
                ModelState.AddModelError(string.Empty, "Invalid voucher sheet data.");
                Sheet = await BuildSheetAsync(RecordDate);
                return Page();
            }

            var validVouchers = NormalizeAndValidateVouchers(postedSheet.Vouchers);

            if (!validVouchers.Any())
            {
                ModelState.AddModelError(string.Empty, "Please add at least one valid voucher.");
                Sheet = await BuildSheetAsync(RecordDate);
                return Page();
            }

            var duplicateNumbers = validVouchers
                .GroupBy(x => x.VoucherNumber.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicateNumbers.Any())
            {
                ModelState.AddModelError(string.Empty, "Duplicate voucher number found in the sheet: " + string.Join(", ", duplicateNumbers));
                Sheet = await BuildSheetAsync(RecordDate);
                return Page();
            }

            try
            {
                var disbursementRecord = await GetOrCreateDisbursementRecordAsync(RecordDate);

                if (disbursementRecord == null || disbursementRecord.Id <= 0)
                {
                    ModelState.AddModelError(string.Empty, "Failed to initialize disbursement record.");
                    Sheet = await BuildSheetAsync(RecordDate);
                    return Page();
                }

                var existingVoucherResponse = await _supabase.Client
                    .From<Voucher>()
                    .Filter("disbursement_record_id", Operator.Equals, disbursementRecord.Id.ToString())
                    .Get();

                var existingVouchers = existingVoucherResponse.Models.ToList();

                if (!existingVouchers.Any())
                {
                    await SaveInitialDisbursementAsync(disbursementRecord, validVouchers);
                }
                else
                {
                    await UpdateCashReturnsOnlyAsync(disbursementRecord, validVouchers);
                }

                await RefreshDisbursementRecordTotalsAsync(disbursementRecord.Id);

                return RedirectToPage("./Create", new { RecordDate = RecordDate.ToString("yyyy-MM-dd") });
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(string.Empty, "An error occurred while saving: " + ex.Message);
                Sheet = await BuildSheetAsync(RecordDate);
                return Page();
            }
        }

        private async Task SaveInitialDisbursementAsync(
            DisbursementRecord disbursementRecord,
            List<VoucherPostVm> validVouchers)
                {
                    foreach (var voucherVm in validVouchers)
                    {
                        var totalReleased = voucherVm.Items.Sum(x => x.Amount);
                        var totalReturned = voucherVm.Items.Sum(x => x.AmountReturned);

                        var newVoucher = new Voucher
                        {
                            DisbursementRecordId = disbursementRecord.Id,
                            VoucherNumber = voucherVm.VoucherNumber.Trim(),
                            Ministry = voucherVm.Ministry.Trim(),
                            Payee = voucherVm.Payee.Trim(),
                            AmountReleased = totalReleased,
                            AmountReturned = totalReturned,
                            CreatedAt = DateTime.UtcNow.AddHours(12),
                            UpdatedAt = DateTime.UtcNow.AddHours(12)
                        };

                        var insertVoucherResponse = await _supabase.Client
                            .From<Voucher>()
                            .Insert(newVoucher);

                        var savedVoucher = insertVoucherResponse.Models.FirstOrDefault();

                        if (savedVoucher == null || savedVoucher.Id <= 0)
                        {
                            throw new Exception($"Failed to save voucher #{voucherVm.VoucherNumber}");
                        }

                        var items = voucherVm.Items
                            .Where(i => !string.IsNullOrWhiteSpace(i.Particular) && (i.Amount > 0 || i.AmountReturned > 0))
                            .Select(i => new VoucherItem
                            {
                                VoucherId = savedVoucher.Id,
                                Particular = i.Particular.Trim(),
                                Amount = i.Amount,
                                AmountReturned = i.AmountReturned,
                                CreatedAt = DateTime.UtcNow.AddHours(12)
                            })
                            .ToList();

                        if (items.Any())
                        {
                            await _supabase.Client
                                .From<VoucherItem>()
                                .Insert(items);
                        }
                    }
                }

        private async Task UpdateCashReturnsOnlyAsync(
            DisbursementRecord disbursementRecord,
            List<VoucherPostVm> postedVouchers)
        {
            var voucherResponse = await _supabase.Client
                .From<Voucher>()
                .Filter("disbursement_record_id", Operator.Equals, disbursementRecord.Id.ToString())
                .Get();

            var existingVouchers = voucherResponse.Models.ToList();

            foreach (var existingVoucher in existingVouchers)
            {
                var postedVoucher = postedVouchers.FirstOrDefault(x =>
                    string.Equals(x.VoucherNumber?.Trim(), existingVoucher.VoucherNumber, StringComparison.OrdinalIgnoreCase));

                if (postedVoucher == null)
                    continue;

                var itemResponse = await _supabase.Client
                    .From<VoucherItem>()
                    .Filter("voucher_id", Operator.Equals, existingVoucher.Id.ToString())
                    .Get();

                var existingItems = itemResponse.Models
                    .OrderBy(x => x.Id)
                    .ToList();

                decimal voucherReturnedTotal = 0m;

                foreach (var existingItem in existingItems)
                {
                    var postedItem = postedVoucher.Items.FirstOrDefault(x =>
                        string.Equals(x.Particular?.Trim(), existingItem.Particular, StringComparison.OrdinalIgnoreCase));

                    if (postedItem == null)
                        continue;

                    var safeReturn = postedItem.AmountReturned;

                    if (safeReturn < 0)
                        safeReturn = 0;

                    if (safeReturn > existingItem.Amount)
                        safeReturn = existingItem.Amount;

                    existingItem.AmountReturned = safeReturn;
                    voucherReturnedTotal += safeReturn;

                    await _supabase.Client
                        .From<VoucherItem>()
                        .Update(existingItem);
                }

                existingVoucher.AmountReturned = voucherReturnedTotal;
                existingVoucher.UpdatedAt = DateTime.UtcNow.AddHours(12);

                await _supabase.Client
                    .From<Voucher>()
                    .Update(existingVoucher);
            }
        }

        private async Task RefreshDisbursementRecordTotalsAsync(long disbursementRecordId)
        {
            var voucherResponse = await _supabase.Client
                .From<Voucher>()
                .Filter("disbursement_record_id", Operator.Equals, disbursementRecordId.ToString())
                .Get();

            var vouchers = voucherResponse.Models.ToList();

            var recordResponse = await _supabase.Client
                .From<DisbursementRecord>()
                .Filter("id", Operator.Equals, disbursementRecordId.ToString())
                .Get();

            var record = recordResponse.Models.FirstOrDefault();
            if (record == null)
                return;

            var safeRecordDate = record.RecordDate.Date.AddHours(12);

            record.RecordDate = safeRecordDate;
            record.TotalReleased = vouchers.Sum(v => v.AmountReleased);
            record.TotalReturned = vouchers.Sum(v => v.AmountReturned);
            record.UpdatedAt = DateTime.UtcNow.AddHours(12);

            await _supabase.Client
                .From<DisbursementRecord>()
                .Update(record);
        }

        public async Task<IActionResult> OnGetBlessingAsync(string date)
        {
            await _supabase.InitializeAsync(true);

            if (string.IsNullOrWhiteSpace(date))
            {
                return new JsonResult(new
                {
                    success = false,
                    blessing = 0m
                });
            }

            var givingResponse = await _supabase.Client
                .From<GivingRecord>()
                .Filter("service_date", Operator.Equals, date)
                .Get();

            var givingRecord = givingResponse.Models.FirstOrDefault();

            return new JsonResult(new
            {
                success = true,
                blessing = givingRecord?.GrandTotal ?? 0m
            });
        }

        private async Task<DisbursementRecord?> GetOrCreateDisbursementRecordAsync(DateTime recordDate)
        {
            var safeRecordDate = recordDate.Date.AddHours(12);
            string dateOnly = safeRecordDate.ToString("yyyy-MM-dd");

            var recordResponse = await _supabase.Client
                .From<DisbursementRecord>()
                .Filter("record_date", Operator.Equals, dateOnly)
                .Get();

            var disbursementRecord = recordResponse.Models.FirstOrDefault();

            var givingResponse = await _supabase.Client
                .From<GivingRecord>()
                .Filter("service_date", Operator.Equals, dateOnly)
                .Get();

            var givingRecord = givingResponse.Models.FirstOrDefault();

            if (disbursementRecord == null)
            {
                var newRecord = new DisbursementRecord
                {
                    RecordDate = safeRecordDate,
                    GivingRecordId = givingRecord?.Id,
                    TotalBlessing = givingRecord?.GrandTotal ?? 0,
                    TotalReleased = 0,
                    TotalReturned = 0,
                    IsClosed = false,
                    CreatedBy = User.Identity?.Name,
                    CreatedAt = DateTime.UtcNow.AddHours(12),
                    UpdatedAt = DateTime.UtcNow.AddHours(12)
                };

                var insertRecord = await _supabase.Client
                    .From<DisbursementRecord>()
                    .Insert(newRecord);

                disbursementRecord = insertRecord.Models.FirstOrDefault();
            }
            else
            {
                disbursementRecord.GivingRecordId = givingRecord?.Id;
                disbursementRecord.TotalBlessing = givingRecord?.GrandTotal ?? 0;
                disbursementRecord.RecordDate = safeRecordDate;
                disbursementRecord.UpdatedAt = DateTime.UtcNow.AddHours(12);

                await _supabase.Client
                    .From<DisbursementRecord>()
                    .Update(disbursementRecord);
            }

            return disbursementRecord;
        }

        public async Task<DisbursementSheetVm> BuildSheetAsync(DateTime recordDate)
        {
            var sheet = new DisbursementSheetVm
            {
                RecordDate = recordDate.Date.AddHours(12),
                Vouchers = new List<VoucherVm>()
            };

            string dateOnly = recordDate.Date.AddHours(12).ToString("yyyy-MM-dd");

            var givingResponse = await _supabase.Client
                .From<GivingRecord>()
                .Filter("service_date", Operator.Equals, dateOnly)
                .Get();

            var givingRecord = givingResponse.Models.FirstOrDefault();
            sheet.Blessing = givingRecord?.GrandTotal ?? 0m;

            var recordResponse = await _supabase.Client
                .From<DisbursementRecord>()
                .Filter("record_date", Operator.Equals, dateOnly)
                .Get();

            var disbursementRecord = recordResponse.Models.FirstOrDefault();

            if (disbursementRecord == null)
            {
                var autoVouchers = await BuildAutoLoadedTemplateVouchersAsync(recordDate.Date);

                sheet.Vouchers = autoVouchers;
                sheet.TotalReleased = autoVouchers.Sum(v => v.AmountReleased);
                sheet.TotalReturned = autoVouchers.Sum(v => v.AmountReturned);
                sheet.AdjustedDisbursement = autoVouchers.Sum(v => v.AdjustedDisbursement);
                sheet.NetCashBalance = sheet.Blessing - sheet.AdjustedDisbursement;
                sheet.AutoLoadedTemplateCount = autoVouchers.Count;

                return sheet;
            }

            sheet.DisbursementRecordId = disbursementRecord.Id;

            var voucherResponse = await _supabase.Client
                .From<Voucher>()
                .Filter("disbursement_record_id", Operator.Equals, disbursementRecord.Id.ToString())
                .Get();

            var vouchers = voucherResponse.Models
                .OrderBy(v => v.VoucherNumber)
                .ToList();

            sheet.HasExistingDisbursement = vouchers.Any();
            sheet.ReturnOnlyMode = vouchers.Any();

            foreach (var voucher in vouchers)
            {
                var itemResponse = await _supabase.Client
                    .From<VoucherItem>()
                    .Filter("voucher_id", Operator.Equals, voucher.Id.ToString())
                    .Get();

                var items = itemResponse.Models
                    .OrderBy(x => x.Id)
                    .Select(x => new VoucherItemVm
                    {
                        Particular = x.Particular,
                        Amount = x.Amount,
                        AmountReturned = x.AmountReturned,
                        NetAmount = x.Amount - x.AmountReturned
                    })
                    .ToList();

                var adjustedDisbursement = voucher.AmountReleased - voucher.AmountReturned;

                sheet.Vouchers.Add(new VoucherVm
                {
                    VoucherNumber = voucher.VoucherNumber,
                    Ministry = voucher.Ministry,
                    Payee = voucher.Payee,
                    AmountReturned = voucher.AmountReturned,
                    AmountReleased = voucher.AmountReleased,
                    AdjustedDisbursement = adjustedDisbursement,
                    TotalInput = voucher.AmountReleased,
                    Items = items
                });
            }
            sheet.TotalReleased = sheet.Vouchers.Sum(v => v.AmountReleased);
            sheet.TotalReturned = sheet.Vouchers.Sum(v => v.AmountReturned);
            sheet.AdjustedDisbursement = sheet.Vouchers.Sum(v => v.AdjustedDisbursement);
            sheet.NetCashBalance = sheet.Blessing - sheet.AdjustedDisbursement;

            sheet.HasExistingDisbursement = vouchers.Any();
            sheet.ReturnOnlyMode = vouchers.Any();

            return sheet;
        }

        private List<VoucherPostVm>NormalizeAndValidateVouchers(List<VoucherPostVm>? vouchers)
        {
            vouchers ??= new List<VoucherPostVm>();

            var result = new List<VoucherPostVm>();

            foreach (var voucher in vouchers)
            {
                if (voucher == null)
                    continue;

                voucher.VoucherNumber = voucher.VoucherNumber?.Trim() ?? "";
                voucher.Ministry = voucher.Ministry?.Trim() ?? "";
                voucher.Payee = voucher.Payee?.Trim() ?? "";
                voucher.Items ??= new List<VoucherItemPostVm>();

                var cleanedItems = voucher.Items
                    .Where(i => i != null)
                    .Select(i => new VoucherItemPostVm
                    {
                        Particular = i.Particular?.Trim() ?? "",
                        Amount = i.Amount,
                        AmountReturned = i.AmountReturned
                    })
                    .Where(i => !string.IsNullOrWhiteSpace(i.Particular))
                    .ToList();

                bool hasHeader =
                    !string.IsNullOrWhiteSpace(voucher.VoucherNumber) &&
                    !string.IsNullOrWhiteSpace(voucher.Ministry) &&
                    !string.IsNullOrWhiteSpace(voucher.Payee);

                if (!hasHeader || !cleanedItems.Any())
                    continue;

                if (cleanedItems.Count == 1)
                {
                    var onlyItem = cleanedItems[0];

                    decimal lineAmount = onlyItem.Amount;
                    decimal totalInput = voucher.TotalInput ?? 0m;

                    decimal effectiveAmount = totalInput > 0 ? totalInput : lineAmount;

                    if (effectiveAmount <= 0)
                        continue;

                    if (onlyItem.AmountReturned < 0)
                        onlyItem.AmountReturned = 0;

                    if (onlyItem.AmountReturned > effectiveAmount)
                        onlyItem.AmountReturned = effectiveAmount;

                    onlyItem.Amount = effectiveAmount;

                    voucher.Items = new List<VoucherItemPostVm> { onlyItem };
                    voucher.TotalInput = effectiveAmount;

                    result.Add(voucher);
                    continue;
                }

                var multiItems = cleanedItems
                    .Where(i => i.Amount > 0 || i.AmountReturned > 0)
                    .Select(i => new VoucherItemPostVm
                    {
                        Particular = i.Particular,
                        Amount = i.Amount,
                        AmountReturned = i.AmountReturned > i.Amount ? i.Amount : i.AmountReturned
                    })
                    .Where(i => i.Amount > 0)
                    .ToList();

                if (multiItems.Count < 2)
                    continue;

                voucher.Items = multiItems;
                voucher.TotalInput = multiItems.Sum(i => i.Amount);

                result.Add(voucher);
            }

            return result;
        }

        private async Task<List<TemplatePayloadVm>> LoadTemplatesAsync()
        {
            var templateResponse = await _supabase.Client
                .From<DisbursementTemplate>()
                .Filter("is_active", Operator.Equals, "true")
                .Get();

            var templates = templateResponse.Models
                .OrderBy(x => x.TemplateName)
                .ToList();

            var result = new List<TemplatePayloadVm>();

            foreach (var template in templates)
            {
                var itemResponse = await _supabase.Client
                    .From<DisbursementTemplateItem>()
                    .Filter("template_id", Operator.Equals, template.Id.ToString())
                    .Get();

                var items = itemResponse.Models
                    .OrderBy(x => x.LineNo)
                    .Select(x => new TemplateItemPayloadVm
                    {
                        LineNo = x.LineNo,
                        Particular = x.Particular,
                        Amount = x.Amount
                    })
                    .ToList();

                result.Add(new TemplatePayloadVm
                {
                    Id = template.Id,
                    TemplateName = template.TemplateName,
                    Ministry = template.Ministry,
                    Payee = template.Payee,
                    TotalInput = items.Sum(x => x.Amount),
                    AutoApply = template.AutoApply,
                    RecurrenceType = template.RecurrenceType ?? "",
                    WeekOfMonth = template.WeekOfMonth,
                    Items = items
                });
            }

            return result;
        }
        public async Task<IActionResult> OnPostSaveTemplateAsync([FromBody] SaveTemplateRequest request)
        {
            await _supabase.InitializeAsync(true);

            if (request == null)
            {
                return new JsonResult(new { success = false, message = "Invalid template request." });
            }

            request.TemplateName = request.TemplateName?.Trim() ?? "";
            request.Ministry = request.Ministry?.Trim() ?? "";
            request.Payee = request.Payee?.Trim() ?? "";
            request.Items ??= new List<TemplateItemPayloadVm>();

            var validItems = request.Items
                .Where(x => !string.IsNullOrWhiteSpace(x.Particular))
                .Select((x, idx) => new TemplateItemPayloadVm
                {
                    LineNo = idx + 1,
                    Particular = x.Particular.Trim(),
                    Amount = x.Amount < 0 ? 0 : x.Amount
                })
                .Where(x => x.Amount > 0)
                .ToList();

            if (string.IsNullOrWhiteSpace(request.TemplateName) ||
                string.IsNullOrWhiteSpace(request.Ministry) ||
                string.IsNullOrWhiteSpace(request.Payee) ||
                !validItems.Any())
            {
                return new JsonResult(new { success = false, message = "Please complete template name, ministry, payee, and at least one valid item." });
            }

            if (request.TemplateId <= 0)
            {
                var template = new DisbursementTemplate
                {
                    TemplateName = request.TemplateName,
                    Ministry = request.Ministry,
                    Payee = request.Payee,
                    AutoApply = request.AutoApply,
                    RecurrenceType = request.AutoApply ? request.RecurrenceType?.Trim() : null,
                    WeekOfMonth = request.AutoApply && request.RecurrenceType == "monthly_week"
                        ? request.WeekOfMonth
                        : null,
                    IsActive = true,
                    CreatedBy = User.Identity?.Name,
                    CreatedAt = DateTime.UtcNow.AddHours(12),
                    UpdatedAt = DateTime.UtcNow.AddHours(12)
                };
                var insertTemplateResponse = await _supabase.Client
                    .From<DisbursementTemplate>()
                    .Insert(template);

                var savedTemplate = insertTemplateResponse.Models.FirstOrDefault();

                if (savedTemplate == null || savedTemplate.Id <= 0)
                {
                    return new JsonResult(new { success = false, message = "Failed to save template header." });
                }

                var itemEntities = validItems.Select(x => new DisbursementTemplateItem
                {
                    TemplateId = savedTemplate.Id,
                    LineNo = x.LineNo,
                    Particular = x.Particular,
                    Amount = x.Amount
                }).ToList();

                await _supabase.Client
                    .From<DisbursementTemplateItem>()
                    .Insert(itemEntities);
            }
            else
            {
                var templateResponse = await _supabase.Client
                    .From<DisbursementTemplate>()
                    .Filter("id", Operator.Equals, request.TemplateId.ToString())
                    .Get();

                var existingTemplate = templateResponse.Models.FirstOrDefault();

                if (existingTemplate == null)
                {
                    return new JsonResult(new { success = false, message = "Template not found." });
                }

                existingTemplate.TemplateName = request.TemplateName;
                existingTemplate.Ministry = request.Ministry;
                existingTemplate.Payee = request.Payee;
                existingTemplate.AutoApply = request.AutoApply;
                existingTemplate.RecurrenceType = request.AutoApply ? request.RecurrenceType?.Trim() : null;
                existingTemplate.WeekOfMonth = request.AutoApply && request.RecurrenceType == "monthly_week"
                    ? request.WeekOfMonth
                    : null;
                existingTemplate.UpdatedAt = DateTime.UtcNow.AddHours(12);

                await _supabase.Client
                    .From<DisbursementTemplate>()
                    .Update(existingTemplate);

                var existingItemsResponse = await _supabase.Client
                    .From<DisbursementTemplateItem>()
                    .Filter("template_id", Operator.Equals, existingTemplate.Id.ToString())
                    .Get();

                foreach (var oldItem in existingItemsResponse.Models)
                {
                    await _supabase.Client
                        .From<DisbursementTemplateItem>()
                        .Delete(oldItem);
                }

                var newItems = validItems.Select(x => new DisbursementTemplateItem
                {
                    TemplateId = existingTemplate.Id,
                    LineNo = x.LineNo,
                    Particular = x.Particular,
                    Amount = x.Amount
                }).ToList();

                await _supabase.Client
                    .From<DisbursementTemplateItem>()
                    .Insert(newItems);
            }

            return new JsonResult(new { success = true, message = "Template saved successfully." });
        }

        public async Task<IActionResult> OnGetTemplatesAsync()
        {
            await _supabase.InitializeAsync(true);
            var templates = await LoadTemplatesAsync();

            return new JsonResult(new
            {
                success = true,
                data = templates
            });
        }

        private int GetWeekOfMonth(DateTime date)
        {
            int day = date.Day;
            return ((day - 1) / 7) + 1;
        }

        public bool IsLastWeekOfMonth(DateTime date)
        {
            return date.AddDays(7).Month != date.Month;
        }

        private bool TemplateMatchesDate(DisbursementTemplate template, DateTime date)
        {
            if (!template.AutoApply || string.IsNullOrWhiteSpace(template.RecurrenceType))
                return false;

            if (template.RecurrenceType == "weekly")
                return true;

            if (template.RecurrenceType == "monthly_week")
            {
                if (template.WeekOfMonth == -1)
                    return IsLastWeekOfMonth(date);

                return GetWeekOfMonth(date) == template.WeekOfMonth;
            }

            return false;
        }
        
        private async Task<List<VoucherVm>> BuildAutoLoadedTemplateVouchersAsync(DateTime recordDate)
        {
            var templateResponse = await _supabase.Client
                .From<DisbursementTemplate>()
                .Filter("is_active", Operator.Equals, "true")
                .Get();

            var templates = templateResponse.Models.ToList();
            var result = new List<VoucherVm>();

            foreach (var template in templates)
            {
                if (!TemplateMatchesDate(template, recordDate))
                    continue;

                var itemResponse = await _supabase.Client
                    .From<DisbursementTemplateItem>()
                    .Filter("template_id", Operator.Equals, template.Id.ToString())
                    .Get();

                var items = itemResponse.Models
                    .OrderBy(x => x.LineNo)
                    .Select(x => new VoucherItemVm
                    {
                        Particular = x.Particular,
                        Amount = x.Amount,
                        AmountReturned = 0,
                        NetAmount = x.Amount
                    })
                    .ToList();

                if (!items.Any())
                    continue;

                result.Add(new VoucherVm
                {
                    VoucherNumber = "",
                    Ministry = template.Ministry,
                    Payee = template.Payee,
                    AmountReturned = 0,
                    AmountReleased = items.Sum(x => x.Amount),
                    AdjustedDisbursement = items.Sum(x => x.Amount),
                    TotalInput = items.Count == 1 ? items[0].Amount : items.Sum(x => x.Amount),
                    Items = items
                });
            }

            return result;
        }
    }

    public class DisbursementSheetVm
    {
        public long DisbursementRecordId { get; set; }
        public DateTime RecordDate { get; set; }
        public decimal Blessing { get; set; }
        public decimal TotalReleased { get; set; }
        public decimal TotalReturned { get; set; }
        public decimal AdjustedDisbursement { get; set; }
        public decimal NetCashBalance { get; set; }
        public bool HasExistingDisbursement { get; set; }
        public bool ReturnOnlyMode { get; set; }
        public List<VoucherVm> Vouchers { get; set; } = new();

        public int AutoLoadedTemplateCount { get; set; }
        public bool HasAutoLoadedTemplates => AutoLoadedTemplateCount > 0;
    }

    public class VoucherVm
    {
        public string VoucherNumber { get; set; } = "";
        public string Ministry { get; set; } = "";
        public string Payee { get; set; } = "";
        public decimal AmountReturned { get; set; }
        public decimal AmountReleased { get; set; }
        public decimal? TotalInput { get; set; }
        public decimal AdjustedDisbursement { get; set; }
        public List<VoucherItemVm> Items { get; set; } = new();
    }

    public class VoucherItemVm
    {
        public string Particular { get; set; } = "";
        public decimal Amount { get; set; }
        public decimal AmountReturned { get; set; }
        public decimal NetAmount { get; set; }
    }

    public class DisbursementSheetPostVm
    {
        public List<VoucherPostVm> Vouchers { get; set; } = new();
    }

    public class VoucherPostVm
    {
        public string VoucherNumber { get; set; } = "";
        public string Ministry { get; set; } = "";
        public string Payee { get; set; } = "";
        public decimal? TotalInput { get; set; }
        public List<VoucherItemPostVm> Items { get; set; } = new();
    }
    public class VoucherItemPostVm
    {
        public string Particular { get; set; } = "";
        public decimal Amount { get; set; }
        public decimal AmountReturned { get; set; }
    }
    public class TemplatePayloadVm
    {
        public long Id { get; set; }
        public string TemplateName { get; set; } = "";
        public string Ministry { get; set; } = "";
        public string Payee { get; set; } = "";
        public decimal TotalInput { get; set; }
        public List<TemplateItemPayloadVm> Items { get; set; } = new();
        public bool AutoApply { get; set; }
        public string RecurrenceType { get; set; } = "";
        public int? WeekOfMonth { get; set; }
    }

    public class TemplateItemPayloadVm
    {
        public int LineNo { get; set; }
        public string Particular { get; set; } = "";
        public decimal Amount { get; set; }
    }

    public class SaveTemplateRequest
    {
        public long TemplateId { get; set; }
        public string TemplateName { get; set; } = "";
        public string Ministry { get; set; } = "";
        public string Payee { get; set; } = "";
        public List<TemplateItemPayloadVm> Items { get; set; } = new();

        public bool AutoApply { get; set; }
        public string RecurrenceType { get; set; } = "";
        public int? WeekOfMonth { get; set; }
    }
}