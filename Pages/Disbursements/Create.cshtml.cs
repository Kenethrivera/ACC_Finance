using acc_finance.Models;
using acc_finance.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using static Supabase.Postgrest.Constants;

namespace acc_finance.Pages.Disbursements
{
    [Authorize(Roles = "Admin")]
    public class CreateModel : PageModel
    {
        private readonly SupabaseService _supabase;
        private readonly IMemoryCache _cache;

        public CreateModel(SupabaseService supabase, IMemoryCache cache)
        {
            _supabase = supabase;
            _cache = cache;
        }

        [BindProperty(SupportsGet = true)]
        public DateTime RecordDate { get; set; } = DateTime.Today;

        [BindProperty]
        public string SheetJson { get; set; } = "";

        public DisbursementSheetVm Sheet { get; set; } = new();
        public List<TemplatePayloadVm> FixedTemplates { get; set; } = new();

        public async Task<IActionResult> OnGetAsync()
        {
            await _supabase.InitializeAsync(true);

            var qsDate = Request.Query["RecordDate"].ToString();
            if (string.IsNullOrEmpty(qsDate))
            {
                var savedDateStr = HttpContext.Session.GetString("ActiveDisbursementDate");
                if (!string.IsNullOrEmpty(savedDateStr) && DateTime.TryParse(savedDateStr, out DateTime parsedDate))
                {
                    if (parsedDate.Date != DateTime.Today)
                    {
                        return RedirectToPage(new { RecordDate = parsedDate.ToString("yyyy-MM-dd") });
                    }
                }
            }
            else
            {
                HttpContext.Session.SetString("ActiveDisbursementDate", RecordDate.ToString("yyyy-MM-dd"));
            }

            Sheet = await BuildSheetAsync(RecordDate);
            FixedTemplates = await LoadTemplatesAsync();

            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            await _supabase.InitializeAsync(true);

            if (RecordDate == DateTime.MinValue)
            {
                ModelState.AddModelError(string.Empty, "Please choose a valid date.");
                Sheet = await BuildSheetAsync(RecordDate);
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
                .Where(g => g.Count() > 1 && !string.IsNullOrWhiteSpace(g.Key))
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
                    await SyncDisbursementVouchersAsync(disbursementRecord, validVouchers, existingVouchers);
                }

                await RefreshDisbursementRecordTotalsAsync(disbursementRecord.Id);

                HttpContext.Session.SetString("ActiveDisbursementDate", RecordDate.ToString("yyyy-MM-dd"));

                // COMPREHENSIVE CACHE INVALIDATION
                string targetMonth = RecordDate.ToString("yyyy-MM");
                DateTime firstDay = new DateTime(RecordDate.Year, RecordDate.Month, 1);
                DateTime lastDay = firstDay.AddMonths(1).AddDays(-1);

                _cache.Remove("Dashboard_Live_Setup");
                _cache.Remove($"SystemReport_{targetMonth}");
                _cache.Remove($"SummaryTable_{RecordDate.Year}");
                _cache.Remove($"DashBalances_ExtraCash_{DateTime.Today:yyyyMMdd}");

                _cache.Remove($"WalletMonthData_{targetMonth}");
                _cache.Remove($"PledgeHistory_{targetMonth}");
                _cache.Remove($"PledgeExp_{lastDay:yyyyMMdd}");

                _cache.Remove($"DashInc_{firstDay:yyyyMMdd}_{lastDay:yyyyMMdd}");
                _cache.Remove($"DashExp_{firstDay:yyyyMMdd}_{lastDay:yyyyMMdd}");

                DateTime epochStart = new DateTime(2000, 1, 1);
                _cache.Remove($"DashExp_{epochStart:yyyyMMdd}_{firstDay.AddDays(-1):yyyyMMdd}");
                _cache.Remove($"DashInc_{epochStart:yyyyMMdd}_{firstDay.AddDays(-1):yyyyMMdd}");

                return RedirectToPage("./Create", new { RecordDate = RecordDate.ToString("yyyy-MM-dd") });
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(string.Empty, "An error occurred while saving: " + ex.Message);
                Sheet = await BuildSheetAsync(RecordDate);
                return Page();
            }
        }

        private async Task SaveInitialDisbursementAsync(DisbursementRecord disbursementRecord, List<VoucherPostVm> validVouchers)
        {
            var newVouchers = validVouchers.Select(v => new Voucher
            {
                DisbursementRecordId = disbursementRecord.Id,
                VoucherNumber = v.VoucherNumber.Trim(),
                Ministry = v.Ministry.Trim(),
                Payee = v.Payee.Trim(),
                AmountReleased = v.Items.Sum(x => x.Amount),
                AmountReturned = v.Items.Sum(x => x.AmountReturned),
                CreatedAt = DateTime.UtcNow.AddHours(12),
                UpdatedAt = DateTime.UtcNow.AddHours(12)
            }).ToList();

            if (!newVouchers.Any()) return;

            var insertVoucherResponse = await _supabase.Client.From<Voucher>().Insert(newVouchers);
            var savedVouchers = insertVoucherResponse.Models;

            var allItemsToInsert = new List<VoucherItem>();

            foreach (var originalVm in validVouchers)
            {
                var savedVoucher = savedVouchers.FirstOrDefault(v => v.VoucherNumber == originalVm.VoucherNumber.Trim() && v.Payee == originalVm.Payee.Trim());

                if (savedVoucher != null)
                {
                    var itemsForThisVoucher = originalVm.Items
                        .Where(item => !string.IsNullOrWhiteSpace(item.Particular) && (item.Amount > 0 || item.AmountReturned > 0))
                        .Select(item => new VoucherItem
                        {
                            VoucherId = savedVoucher.Id,
                            Particular = item.Particular.Trim(),
                            FundSource = item.FundSource.Trim(),
                            Amount = item.Amount,
                            AmountReturned = item.AmountReturned,
                            CreatedAt = DateTime.UtcNow.AddHours(12)
                        });

                    allItemsToInsert.AddRange(itemsForThisVoucher);
                }
            }

            if (allItemsToInsert.Any())
            {
                await _supabase.Client.From<VoucherItem>().Insert(allItemsToInsert);
            }
        }

        private async Task SyncDisbursementVouchersAsync(DisbursementRecord disbursementRecord, List<VoucherPostVm> postedVouchers, List<Voucher> existingVouchers)
        {
            var postedKeys = postedVouchers
                .Select(x => $"{x.VoucherNumber?.Trim()}|{x.Ministry?.Trim()}|{x.Payee?.Trim()}")
                .ToList();

            var toDelete = existingVouchers
                .Where(v => !postedKeys.Contains($"{v.VoucherNumber}|{v.Ministry}|{v.Payee}"))
                .ToList();

            var toDeleteIds = toDelete.Select(v => v.Id).ToList();

            if (toDeleteIds.Any())
            {
                await _supabase.Client.From<VoucherItem>().Filter("voucher_id", Operator.In, toDeleteIds).Delete();
                await _supabase.Client.From<Voucher>().Filter("id", Operator.In, toDeleteIds).Delete();
            }

            var vouchersToInsert = new List<VoucherPostVm>();
            var vouchersToUpdate = new List<Voucher>();
            var existingIdsToUpdate = new List<long>();

            foreach (var pv in postedVouchers)
            {
                var existing = existingVouchers.FirstOrDefault(v =>
                    v.VoucherNumber == pv.VoucherNumber?.Trim() &&
                    v.Ministry == pv.Ministry?.Trim() &&
                    v.Payee == pv.Payee?.Trim());

                if (existing == null)
                {
                    vouchersToInsert.Add(pv);
                }
                else
                {
                    existing.AmountReleased = pv.Items.Sum(x => x.Amount);
                    existing.AmountReturned = pv.Items.Sum(x => x.AmountReturned);
                    existing.UpdatedAt = DateTime.UtcNow.AddHours(12);

                    vouchersToUpdate.Add(existing);
                    existingIdsToUpdate.Add(existing.Id);
                }
            }

            if (vouchersToUpdate.Any())
            {
                await _supabase.Client.From<Voucher>().Upsert(vouchersToUpdate);
                await _supabase.Client.From<VoucherItem>().Filter("voucher_id", Operator.In, existingIdsToUpdate).Delete();

                var newItemsForUpdatedVouchers = new List<VoucherItem>();
                foreach (var existingV in vouchersToUpdate)
                {
                    var pv = postedVouchers.First(v => v.VoucherNumber?.Trim() == existingV.VoucherNumber && v.Ministry?.Trim() == existingV.Ministry && v.Payee?.Trim() == existingV.Payee);
                    newItemsForUpdatedVouchers.AddRange(pv.Items.Select(i => new VoucherItem
                    {
                        VoucherId = existingV.Id,
                        Particular = i.Particular?.Trim(),
                        FundSource = i.FundSource?.Trim() ?? "General",
                        Amount = i.Amount,
                        AmountReturned = i.AmountReturned,
                        CreatedAt = DateTime.UtcNow.AddHours(12)
                    }));
                }

                if (newItemsForUpdatedVouchers.Any())
                {
                    await _supabase.Client.From<VoucherItem>().Insert(newItemsForUpdatedVouchers);
                }
            }

            if (vouchersToInsert.Any())
            {
                await SaveInitialDisbursementAsync(disbursementRecord, vouchersToInsert);
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
            if (record == null) return;

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
                return new JsonResult(new { success = false, blessing = 0m });
            }

            var givingResponse = await _supabase.Client
                .From<GivingRecord>()
                .Filter("service_date", Operator.Equals, date)
                .Get();

            var givingRecord = givingResponse.Models.FirstOrDefault();

            return new JsonResult(new { success = true, blessing = givingRecord?.GrandTotal ?? 0m });
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

        private List<VoucherPostVm> NormalizeAndValidateVouchers(List<VoucherPostVm>? vouchers)
        {
            vouchers ??= new List<VoucherPostVm>();
            var result = new List<VoucherPostVm>();

            foreach (var voucher in vouchers)
            {
                if (voucher == null) continue;

                voucher.VoucherNumber = voucher.VoucherNumber?.Trim() ?? "";
                voucher.Ministry = voucher.Ministry?.Trim() ?? "";
                voucher.Payee = voucher.Payee?.Trim() ?? "";
                voucher.Items ??= new List<VoucherItemPostVm>();

                var cleanedItems = voucher.Items
                    .Where(i => i != null)
                    .Select(i => new VoucherItemPostVm
                    {
                        Particular = i.Particular?.Trim() ?? "",
                        FundSource = string.IsNullOrWhiteSpace(i.FundSource) ? "General" : i.FundSource.Trim(),
                        Amount = i.Amount,
                        AmountReturned = i.AmountReturned
                    })
                    .Where(i => !string.IsNullOrWhiteSpace(i.Particular))
                    .ToList();

                bool hasHeader =
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

                    if (effectiveAmount <= 0) continue;

                    if (onlyItem.AmountReturned < 0) onlyItem.AmountReturned = 0;
                    if (onlyItem.AmountReturned > effectiveAmount) onlyItem.AmountReturned = effectiveAmount;

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
                        FundSource = i.FundSource,
                        Amount = i.Amount,
                        AmountReturned = i.AmountReturned > i.Amount ? i.Amount : i.AmountReturned
                    })
                    .Where(i => i.Amount > 0)
                    .ToList();

                if (multiItems.Count < 2) continue;

                voucher.Items = multiItems;
                voucher.TotalInput = multiItems.Sum(i => i.Amount);

                result.Add(voucher);
            }

            return result;
        }

        private async Task<List<TemplatePayloadVm>> LoadTemplatesAsync()
        {
            if (!_cache.TryGetValue("DisbursementTemplates", out List<TemplatePayloadVm> cachedTemplates))
            {
                var templateResponse = await _supabase.Client
                    .From<DisbursementTemplate>()
                    .Filter("is_active", Operator.Equals, "true")
                    .Get();

                var templates = templateResponse.Models
                    .OrderBy(x => x.RecurrenceType)
                    .ThenBy(x => x.WeekOfMonth)
                    .ThenBy(x => x.Ministry)
                    .ToList();

                var templateIds = templates.Select(t => t.Id).ToList();
                var allItems = new List<DisbursementTemplateItem>();

                if (templateIds.Any())
                {
                    var itemResponse = await _supabase.Client
                        .From<DisbursementTemplateItem>()
                        .Filter("template_id", Operator.In, templateIds)
                        .Get();
                    allItems = itemResponse.Models.ToList();
                }

                var result = new List<TemplatePayloadVm>();

                foreach (var template in templates)
                {
                    var items = allItems
                        .Where(x => x.TemplateId == template.Id)
                        .OrderBy(x => x.LineNo)
                        .Select(x => new TemplateItemPayloadVm
                        {
                            LineNo = x.LineNo,
                            Particular = x.Particular,
                            FundSource = x.FundSource ?? "General",
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

                cachedTemplates = result;
                var templateOptions = new MemoryCacheEntryOptions()
                    .SetSize(5)
                    .SetAbsoluteExpiration(TimeSpan.FromHours(1));
                _cache.Set("DisbursementTemplates", cachedTemplates, templateOptions);
            }

            return cachedTemplates ?? new List<TemplatePayloadVm>();
        }

        public async Task<IActionResult> OnPostSaveTemplateAsync([FromBody] SaveTemplateRequest request)
        {
            await _supabase.InitializeAsync(true);

            if (request == null)
            {
                return new JsonResult(new { success = false, message = "Invalid template request." });
            }

            request.Ministry = request.Ministry?.Trim() ?? "";
            request.Payee = request.Payee?.Trim() ?? "";
            request.RecurrenceType = request.RecurrenceType?.Trim() ?? "";
            request.Items ??= new List<TemplateItemPayloadVm>();

            var validItems = request.Items
                .Where(x => !string.IsNullOrWhiteSpace(x.Particular))
                .Select((x, idx) => new TemplateItemPayloadVm
                {
                    LineNo = idx + 1,
                    Particular = x.Particular.Trim(),
                    FundSource = string.IsNullOrWhiteSpace(x.FundSource) ? "General" : x.FundSource.Trim(),
                    Amount = x.Amount < 0 ? 0 : x.Amount
                })
                .Where(x => x.Amount > 0)
                .ToList();

            if (string.IsNullOrWhiteSpace(request.RecurrenceType) ||
                request.WeekOfMonth <= 0 ||
                string.IsNullOrWhiteSpace(request.Ministry) ||
                string.IsNullOrWhiteSpace(request.Payee) ||
                !validItems.Any())
            {
                return new JsonResult(new { success = false, message = "Please select week schedule, complete ministry, payee, and at least one valid item." });
            }

            if (request.TemplateId <= 0)
            {
                var template = new DisbursementTemplate
                {
                    TemplateName = "Schedule",
                    Ministry = request.Ministry,
                    Payee = request.Payee,
                    AutoApply = true,
                    RecurrenceType = request.RecurrenceType,
                    WeekOfMonth = request.WeekOfMonth,
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
                    FundSource = x.FundSource,
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

                existingTemplate.Ministry = request.Ministry;
                existingTemplate.Payee = request.Payee;
                existingTemplate.RecurrenceType = request.RecurrenceType;
                existingTemplate.WeekOfMonth = request.WeekOfMonth;
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
                    FundSource = x.FundSource,
                    Amount = x.Amount
                }).ToList();

                await _supabase.Client
                    .From<DisbursementTemplateItem>()
                    .Insert(newItems);
            }
            _cache.Remove("DisbursementTemplates");
            return new JsonResult(new { success = true, message = "Template saved successfully." });
        }

        public class DeleteTemplateRequest { public long TemplateId { get; set; } }

        public async Task<DisbursementSheetVm> BuildSheetAsync(DateTime recordDate)
        {
            var sheet = new DisbursementSheetVm
            {
                RecordDate = recordDate.Date.AddHours(12),
                Vouchers = new List<VoucherVm>()
            };

            string dateOnly = recordDate.Date.AddHours(12).ToString("yyyy-MM-dd");

            var givingResponse = await _supabase.Client.From<GivingRecord>()
                .Filter("service_date", Operator.Equals, dateOnly).Get();
            var givingRecord = givingResponse.Models.FirstOrDefault();
            sheet.Blessing = givingRecord?.GrandTotal ?? 0m;

            var recordResponse = await _supabase.Client.From<DisbursementRecord>()
                .Filter("record_date", Operator.Equals, dateOnly).Get();
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

            var voucherResponse = await _supabase.Client.From<Voucher>()
                .Filter("disbursement_record_id", Operator.Equals, disbursementRecord.Id.ToString())
                .Get();

            var vouchers = voucherResponse.Models.OrderBy(v => v.VoucherNumber).ToList();

            sheet.HasExistingDisbursement = vouchers.Any();
            sheet.ReturnOnlyMode = false;

            var voucherIds = vouchers.Select(v => v.Id).ToList();
            var allVoucherItems = new List<VoucherItem>();

            if (voucherIds.Any())
            {
                var itemResponse = await _supabase.Client.From<VoucherItem>()
                    .Filter("voucher_id", Operator.In, voucherIds).Get();
                allVoucherItems = itemResponse.Models.ToList();
            }

            foreach (var voucher in vouchers)
            {
                var items = allVoucherItems
                    .Where(x => x.VoucherId == voucher.Id)
                    .OrderBy(x => x.Id)
                    .Select(x => new VoucherItemVm
                    {
                        Particular = x.Particular,
                        FundSource = x.FundSource ?? "General",
                        Amount = x.Amount,
                        AmountReturned = x.AmountReturned,
                        NetAmount = x.Amount - x.AmountReturned
                    }).ToList();

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

            return sheet;
        }

        private int GetSundaysInMonth(DateTime date)
        {
            int count = 0;
            int days = DateTime.DaysInMonth(date.Year, date.Month);
            for (int i = 1; i <= days; i++)
            {
                if (new DateTime(date.Year, date.Month, i).DayOfWeek == DayOfWeek.Sunday)
                    count++;
            }
            return count;
        }

        private int GetSundayIndex(DateTime date)
        {
            DateTime temp = date;
            while (temp.DayOfWeek != DayOfWeek.Sunday && temp.Day > 1)
            {
                temp = temp.AddDays(-1);
            }

            int count = 0;
            for (int i = 1; i <= temp.Day; i++)
            {
                if (new DateTime(temp.Year, temp.Month, i).DayOfWeek == DayOfWeek.Sunday)
                    count++;
            }
            return count == 0 ? 1 : count;
        }

        private async Task<List<VoucherVm>> BuildAutoLoadedTemplateVouchersAsync(DateTime recordDate)
        {
            var templateResponse = await _supabase.Client
                .From<DisbursementTemplate>()
                .Filter("is_active", Operator.Equals, "true")
                .Get();

            var templates = templateResponse.Models.ToList();
            var result = new List<VoucherVm>();

            int totalSundaysInMonth = GetSundaysInMonth(recordDate);
            string expectedRecurrenceType = totalSundaysInMonth == 5 ? "5_week" : "4_week";
            int currentSundayIndex = GetSundayIndex(recordDate);

            var applicableTemplates = templates
                .Where(t => t.RecurrenceType == expectedRecurrenceType && t.WeekOfMonth == currentSundayIndex)
                .ToList();

            var templateIds = applicableTemplates.Select(t => t.Id).ToList();
            var allItems = new List<DisbursementTemplateItem>();

            if (templateIds.Any())
            {
                var itemResponse = await _supabase.Client
                    .From<DisbursementTemplateItem>()
                    .Filter("template_id", Operator.In, templateIds)
                    .Get();
                allItems = itemResponse.Models.ToList();
            }

            foreach (var template in applicableTemplates)
            {
                var items = allItems
                    .Where(x => x.TemplateId == template.Id)
                    .OrderBy(x => x.LineNo)
                    .Select(x => new VoucherItemVm
                    {
                        Particular = x.Particular,
                        FundSource = x.FundSource ?? "General",
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

    // (VoucherVm, VoucherItemVm, DisbursementSheetPostVm and Templates classes remain exactly the same...)
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
        public string FundSource { get; set; } = "General";
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
        public string FundSource { get; set; } = "General";
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
        public string FundSource { get; set; } = "General";
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