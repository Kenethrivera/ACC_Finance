using Microsoft.AspNetCore.Mvc.RazorPages;
using static Supabase.Postgrest.Constants;
using acc_finance.Models;
using acc_finance.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Authorization;

namespace acc_finance.Pages.Giving
{
    [Authorize(Roles = "Admin")]
    public class EntryModel : PageModel
    {
        private readonly SupabaseService _supabase;
        private readonly IMemoryCache _cache;

        public EntryModel(SupabaseService supabase, IMemoryCache cache)
        {
            _supabase = supabase;
            _cache = cache;
        }

        public List<Member> Members { get; set; } = new();
        public List<GivingEntryRowVm> SummaryRows { get; set; } = new();

        [BindProperty(SupportsGet = true)]
        public long RecordId { get; set; }

        [BindProperty(SupportsGet = true)]
        public long? SelectedMemberId { get; set; }

        [BindProperty]
        public RecordHeaderInput Header { get; set; } = new();

        public GivingRecord? CurrentRecord { get; set; }

        [BindProperty]
        public GivingInput Input { get; set; } = new();

        public decimal TotalTithes { get; set; }
        public decimal TotalOfferings { get; set; }
        public decimal TotalSolomon { get; set; }
        public decimal TotalNoah { get; set; }
        public decimal TotalMission { get; set; }
        public decimal TotalOthers { get; set; }
        public decimal GrandTotal { get; set; }

        public string Message { get; set; } = "";
        public bool IsClosed { get; set; }

        public GivingDenominationInput Denomination { get; set; } = new();
        public decimal DenominationTotal { get; set; }
        public decimal DenominationDifference { get; set; }
        public bool IsDenominationMatched { get; set; }
        public bool DenominationExist { get; set; }

        private async Task<List<Member>> GetCachedMembersAsync()
        {
            if (!_cache.TryGetValue("ActiveMembersList", out List<Member> cachedMembers))
            {
                var memberResponse = await _supabase.Client
                    .From<Member>()
                    .Filter("is_active", Operator.Equals, "true")
                    .Get();

                cachedMembers = memberResponse.Models.OrderBy(m => m.Name).ToList();
                var memberOptions = new MemoryCacheEntryOptions()
                    .SetSize(5) 
                    .SetAbsoluteExpiration(TimeSpan.FromHours(1));
                _cache.Set("ActiveMembersList", cachedMembers, memberOptions);
            }
            return cachedMembers ?? new List<Member>();
        }

        public async Task<IActionResult> OnGetAsync()
        {
            if (RecordId == 0)
            {
                var savedRecordId = HttpContext.Session.GetString("ActiveGivingRecordId");
                if (!string.IsNullOrEmpty(savedRecordId) && long.TryParse(savedRecordId, out long parsedId))
                {
                    return RedirectToPage(new { RecordId = parsedId });
                }
            }
            else
            {
                HttpContext.Session.SetString("ActiveGivingRecordId", RecordId.ToString());
            }

            await LoadPageDataAsync();
            return Page();
        }

        public async Task<IActionResult> OnPostLoadRecordAsync()
        {
            await _supabase.InitializeAsync(true);

            if (Header.ServiceDate == DateTime.MinValue)
            {
                Message = "Please choose a valid date.";
                await LoadPageDataAsync();
                return Page();
            }

            string dateOnly = Header.ServiceDate.Date.ToString("yyyy-MM-dd");

            var query = await _supabase.Client
                .From<GivingRecord>()
                .Filter("service_date", Operator.Equals, dateOnly)
                .Get();

            var record = query.Models.FirstOrDefault();

            if (record == null)
            {
                Message = "No existing record found for the selected date.";
                await LoadPageDataAsync();
                return Page();
            }

            RecordId = record.Id;
            return RedirectToPage(new { RecordId });
        }

        public async Task<IActionResult> OnPostCreateRecordAsync()
        {
            await _supabase.InitializeAsync(true);

            if (Header.ServiceDate == DateTime.MinValue)
            {
                Message = "Please choose a valid date.";
                await LoadPageDataAsync();
                return Page();
            }

            var safeServiceDate = Header.ServiceDate.Date.AddHours(12);
            string dateOnly = safeServiceDate.ToString("yyyy-MM-dd");

            var existingResponse = await _supabase.Client
                .From<GivingRecord>()
                .Filter("service_date", Operator.Equals, dateOnly)
                .Get();

            var existing = existingResponse.Models
                .FirstOrDefault(x => x.RecordCode == Header.RecordCode);

            if (existing != null)
            {
                Message = "That record code and date already exist.";
                await LoadPageDataAsync();
                return Page();
            }

            var newRecord = new GivingRecord
            {
                RecordCode = string.IsNullOrWhiteSpace(Header.RecordCode) ? "CR_01" : Header.RecordCode,
                ServiceDate = safeServiceDate,
                IsClosed = false,
                CreatedBy = User.Identity?.Name,
                CreatedAt = DateTime.UtcNow
            };

            try
            {
                var insertResponse = await _supabase.Client
                    .From<GivingRecord>()
                    .Insert(newRecord);

                var inserted = insertResponse.Models.FirstOrDefault();
                RecordId = inserted?.Id ?? 0;

                return RedirectToPage(new { RecordId });
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("duplicate key") || ex.Message.Contains("already exists"))
                {
                    Message = "Record already exists for this code/date.";
                    await LoadPageDataAsync();
                    return Page();
                }

                Message = "Unexpected Error: " + ex.Message;
                await LoadPageDataAsync();
                return Page();
            }
        }

        private object ToDenominationPayload(DenominationStateVm state)
        {
            return new
            {
                exists = state.Exists,
                qty1000 = state.Input.Qty1000,
                qty500 = state.Input.Qty500,
                qty200 = state.Input.Qty200,
                qty100 = state.Input.Qty100,
                qty50 = state.Input.Qty50,
                qty20Coin = state.Input.Qty20Coin,
                qty20Paper = state.Input.Qty20Paper,
                qty10 = state.Input.Qty10,
                qty5 = state.Input.Qty5,
                qty1 = state.Input.Qty1,
                qty25Cent = state.Input.Qty25Cent,
                qty10Cent = state.Input.Qty10Cent,
                qty5Cent = state.Input.Qty5Cent,
                qty1Cent = state.Input.Qty1Cent,
                total = state.Total,
                target = state.Target,
                difference = state.Difference,
                isMatched = state.IsMatched
            };
        }

        public async Task<IActionResult> OnGetEntryRowAsync(long recordId, string rowKey)
        {
            await _supabase.InitializeAsync(true);

            if (recordId <= 0 || string.IsNullOrWhiteSpace(rowKey))
            {
                return new JsonResult(new { success = false, message = "Invalid row request." });
            }

            var drafts = GetDraftEntries(recordId);
            var draft = drafts.FirstOrDefault(x => x.RowKey == rowKey);

            if (draft != null)
            {
                return new JsonResult(new
                {
                    success = true,
                    rowKey = draft.RowKey,
                    memberId = draft.MemberId,
                    customName = draft.CustomName,
                    displayName = !string.IsNullOrWhiteSpace(draft.CustomName) ? draft.CustomName : "",
                    tithes = draft.Tithes,
                    offerings = draft.Offerings,
                    solomon = draft.Solomon,
                    noah = draft.Noah,
                    mission = draft.Mission,
                    others = draft.Others,
                    othersFund = draft.OthersFund // NEW PER-ROW FIELD
                });
            }

            return new JsonResult(new { success = false, message = "Row not found in draft." });
        }

        public async Task LoadPageDataAsync()
        {
            await _supabase.InitializeAsync(true);

            Members = await GetCachedMembersAsync();

            if (RecordId > 0)
            {
                var recordResponse = await _supabase.Client
                    .From<GivingRecord>()
                    .Filter("id", Operator.Equals, RecordId.ToString())
                    .Get();

                CurrentRecord = recordResponse.Models.FirstOrDefault();

                if (CurrentRecord != null)
                {
                    Header.RecordCode = CurrentRecord.RecordCode;
                    Header.ServiceDate = CurrentRecord.ServiceDate.Date;
                    IsClosed = CurrentRecord.IsClosed;
                }
            }

            var draftEntries = GetDraftEntries(RecordId);

            if (SelectedMemberId.HasValue)
            {
                var draft = draftEntries.FirstOrDefault(x => x.MemberId == SelectedMemberId.Value);

                if (draft != null)
                {
                    Input = new GivingInput
                    {
                        MemberId = draft.MemberId,
                        Tithes = draft.Tithes,
                        Offerings = draft.Offerings,
                        Solomon = draft.Solomon,
                        Noah = draft.Noah,
                        Mission = draft.Mission,
                        Others = draft.Others,
                        OthersFund = draft.OthersFund
                    };
                }
                else if (RecordId > 0)
                {
                    var entryResponse = await _supabase.Client
                        .From<GivingEntry>()
                        .Filter("giving_record_id", Operator.Equals, RecordId.ToString())
                        .Filter("member_id", Operator.Equals, SelectedMemberId.Value.ToString())
                        .Get();

                    var entry = entryResponse.Models.FirstOrDefault();

                    Input = new GivingInput
                    {
                        MemberId = SelectedMemberId.Value,
                        Tithes = entry?.Tithes ?? 0,
                        Offerings = entry?.Offerings ?? 0,
                        Solomon = entry?.Solomon ?? 0,
                        Noah = entry?.Noah ?? 0,
                        Mission = entry?.Mission ?? 0,
                        Others = entry?.Others ?? 0,
                        OthersFund = entry?.OthersFund ?? "General"
                    };
                }
            }

            if (draftEntries.Any())
            {
                SummaryRows = draftEntries
                    .Select(e =>
                    {
                        var memberName = e.MemberId.HasValue
                            ? Members.FirstOrDefault(m => m.Id == e.MemberId.Value)?.Name ?? "(Unknown Member)"
                            : e.CustomName ?? "(Unnamed)";

                        return new GivingEntryRowVm
                        {
                            RowKey = e.RowKey,
                            MemberId = e.MemberId,
                            MemberName = memberName,
                            CustomName = e.CustomName,
                            Tithes = e.Tithes,
                            Offerings = e.Offerings,
                            Solomon = e.Solomon,
                            Noah = e.Noah,
                            Mission = e.Mission,
                            Others = e.Others,
                            OthersFund = e.OthersFund,
                            Total = e.Total
                        };
                    })
                    // Removed .OrderBy to keep chronological order for drafts
                    .ToList();
            }
            else if (RecordId > 0)
            {
                var allEntriesResponse = await _supabase.Client
                    .From<GivingEntry>()
                    .Filter("giving_record_id", Operator.Equals, RecordId.ToString())
                    .Get();

                var allEntries = allEntriesResponse.Models;

                var mappedRows = allEntries
                    .Select(e =>
                    {
                        var memberName = e.MemberId.HasValue
                            ? Members.FirstOrDefault(m => m.Id == e.MemberId.Value)?.Name ?? "(Unknown Member)"
                            : e.EntryName ?? "(Unnamed)";

                        return new GivingEntryRowVm
                        {
                            Id = e.Id,
                            RowKey = $"db-{e.Id}",
                            MemberId = e.MemberId,
                            MemberName = memberName,
                            CustomName = e.EntryName,
                            Tithes = e.Tithes,
                            Offerings = e.Offerings,
                            Solomon = e.Solomon,
                            Noah = e.Noah,
                            Mission = e.Mission,
                            Others = e.Others,
                            OthersFund = e.OthersFund,
                            Total = e.Total
                        };
                    }).ToList();

                // Conditional Sorting Check
                if (IsClosed)
                {
                    SummaryRows = mappedRows.OrderBy(x => x.MemberName).ToList();
                }
                else
                {
                    SummaryRows = mappedRows.OrderBy(x => x.Id).ToList();
                }
            }
            else
            {
                SummaryRows = new List<GivingEntryRowVm>();
            }

            TotalTithes = SummaryRows.Sum(x => x.Tithes);
            TotalOfferings = SummaryRows.Sum(x => x.Offerings);
            TotalSolomon = SummaryRows.Sum(x => x.Solomon);
            TotalNoah = SummaryRows.Sum(x => x.Noah);
            TotalMission = SummaryRows.Sum(x => x.Mission);
            TotalOthers = SummaryRows.Sum(x => x.Others);
            GrandTotal = SummaryRows.Sum(x => x.Total);

            if (RecordId > 0)
            {
                var denominationState = await BuildDenominationStateAsync(RecordId, GrandTotal);
                DenominationExist = denominationState.Exists;
                Denomination = denominationState.Input ?? new GivingDenominationInput();
                DenominationTotal = denominationState.Total;
                DenominationDifference = denominationState.Difference;
                IsDenominationMatched = denominationState.IsMatched;
            }
            else
            {
                DenominationExist = false;
                Denomination = new GivingDenominationInput();
                DenominationTotal = 0;
                DenominationDifference = 0;
                IsDenominationMatched = false;
            }
        }

        private string GetDraftKey(long recordId) => $"giving_draft_{recordId}";

        private List<GivingEntryDraftVm> GetDraftEntries(long recordId)
        {
            if (recordId <= 0) return new List<GivingEntryDraftVm>();

            var json = HttpContext.Session.GetString(GetDraftKey(recordId));

            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<GivingEntryDraftVm>();
            }

            return JsonSerializer.Deserialize<List<GivingEntryDraftVm>>(json)
                   ?? new List<GivingEntryDraftVm>();
        }

        private void SaveDraftEntries(long recordId, List<GivingEntryDraftVm> drafts)
        {
            var json = JsonSerializer.Serialize(drafts);
            HttpContext.Session.SetString(GetDraftKey(recordId), json);
        }

        private void ClearDraftEntries(long recordId)
        {
            HttpContext.Session.Remove(GetDraftKey(recordId));
        }

        private async Task<List<GivingEntryRowVm>> BuildSummaryRowsAsync(long recordId)
        {
            // Check if record is closed first
            var recordResponse = await _supabase.Client
                .From<GivingRecord>()
                .Filter("id", Operator.Equals, recordId.ToString())
                .Get();
            var record = recordResponse.Models.FirstOrDefault();
            bool isClosed = record?.IsClosed ?? false;

            var members = await GetCachedMembersAsync();

            var dbResponse = await _supabase.Client
                .From<GivingEntry>()
                .Filter("giving_record_id", Operator.Equals, recordId.ToString())
                .Get();

            // Ensure DB entries are loaded in chronological order by ID
            var dbRows = dbResponse.Models.OrderBy(x => x.Id).ToList();

            var drafts = GetDraftEntries(recordId).ToList();
            var summary = new List<GivingEntryRowVm>();

            // 1. Add DB rows (override with draft if one exists)
            foreach (var dbRow in dbRows)
            {
                var draft = drafts.FirstOrDefault(d =>
                    (d.MemberId.HasValue && dbRow.MemberId == d.MemberId) ||
                    (!string.IsNullOrEmpty(d.RowKey) && d.RowKey == $"db-{dbRow.Id}"));

                if (draft != null)
                {
                    summary.Add(new GivingEntryRowVm
                    {
                        RowKey = draft.RowKey,
                        MemberId = draft.MemberId,
                        MemberName = draft.MemberId.HasValue ? members.FirstOrDefault(m => m.Id == draft.MemberId)?.Name ?? "(Unknown)" : draft.CustomName ?? "(Unnamed)",
                        CustomName = draft.CustomName,
                        Tithes = draft.Tithes,
                        Offerings = draft.Offerings,
                        Solomon = draft.Solomon,
                        Noah = draft.Noah,
                        Mission = draft.Mission,
                        Others = draft.Others,
                        OthersFund = draft.OthersFund,
                        Total = draft.Total
                    });
                    drafts.Remove(draft);
                }
                else
                {
                    summary.Add(new GivingEntryRowVm
                    {
                        Id = dbRow.Id,
                        RowKey = $"db-{dbRow.Id}",
                        MemberId = dbRow.MemberId,
                        MemberName = dbRow.MemberId.HasValue ? members.FirstOrDefault(m => m.Id == dbRow.MemberId)?.Name ?? "(Unknown)" : dbRow.EntryName ?? "(Unnamed)",
                        CustomName = dbRow.EntryName,
                        Tithes = dbRow.Tithes,
                        Offerings = dbRow.Offerings,
                        Solomon = dbRow.Solomon,
                        Noah = dbRow.Noah,
                        Mission = dbRow.Mission,
                        Others = dbRow.Others,
                        OthersFund = dbRow.OthersFund,
                        Total = dbRow.Total
                    });
                }
            }

            // 2. Add any remaining brand new drafts (these naturally fall to the bottom)
            foreach (var draft in drafts)
            {
                summary.Add(new GivingEntryRowVm
                {
                    RowKey = draft.RowKey,
                    MemberId = draft.MemberId,
                    MemberName = draft.MemberId.HasValue ? members.FirstOrDefault(m => m.Id == draft.MemberId)?.Name ?? "(Unknown)" : draft.CustomName ?? "(Unnamed)",
                    CustomName = draft.CustomName,
                    Tithes = draft.Tithes,
                    Offerings = draft.Offerings,
                    Solomon = draft.Solomon,
                    Noah = draft.Noah,
                    Mission = draft.Mission,
                    Others = draft.Others,
                    OthersFund = draft.OthersFund,
                    Total = draft.Total
                });
            }

            // Conditional Sorting
            if (isClosed)
            {
                return summary.OrderBy(x => x.MemberName).ToList();
            }
            else
            {
                return summary; // Return chronological order
            }
        }

        private TotalsVm BuildTotals(List<GivingEntryRowVm> rows)
        {
            return new TotalsVm
            {
                TotalTithes = rows.Sum(x => x.Tithes),
                TotalOfferings = rows.Sum(x => x.Offerings),
                TotalSolomon = rows.Sum(x => x.Solomon),
                TotalNoah = rows.Sum(x => x.Noah),
                TotalMission = rows.Sum(x => x.Mission),
                TotalOthers = rows.Sum(x => x.Others),
                GrandTotal = rows.Sum(x => x.Total)
            };
        }

        public async Task<IActionResult> OnGetMemberEntryAsync(long recordId, long memberId)
        {
            await _supabase.InitializeAsync(true);

            if (recordId <= 0 || memberId <= 0)
            {
                return new JsonResult(new { success = false, message = "Invalid record or member." });
            }

            var drafts = GetDraftEntries(recordId);
            var draft = drafts.FirstOrDefault(x => x.MemberId == memberId);

            if (draft != null)
            {
                return new JsonResult(new
                {
                    success = true,
                    rowKey = draft.RowKey,
                    memberId = draft.MemberId,
                    customName = draft.CustomName,
                    displayName = draft.CustomName,
                    tithes = draft.Tithes,
                    offerings = draft.Offerings,
                    solomon = draft.Solomon,
                    noah = draft.Noah,
                    mission = draft.Mission,
                    others = draft.Others,
                    othersFund = draft.OthersFund
                });
            }

            var entryResponse = await _supabase.Client
                .From<GivingEntry>()
                .Filter("giving_record_id", Operator.Equals, recordId.ToString())
                .Filter("member_id", Operator.Equals, memberId.ToString())
                .Get();

            var entry = entryResponse.Models.FirstOrDefault();

            return new JsonResult(new
            {
                success = true,
                rowKey = "",
                memberId = memberId,
                customName = entry?.EntryName ?? "",
                displayName = entry?.EntryName ?? "",
                tithes = entry?.Tithes ?? 0,
                offerings = entry?.Offerings ?? 0,
                solomon = entry?.Solomon ?? 0,
                noah = entry?.Noah ?? 0,
                mission = entry?.Mission ?? 0,
                others = entry?.Others ?? 0,
                othersFund = entry?.OthersFund ?? "General"
            });
        }

        private async Task<RecordPayloadData> BuildRecordPayloadAsync()
        {
            DenominationStateVm denomState;

            if (RecordId > 0)
                denomState = await BuildDenominationStateAsync(RecordId, GrandTotal);
            else
                denomState = new DenominationStateVm();

            return new RecordPayloadData
            {
                RecordId = RecordId,
                RecordCode = CurrentRecord?.RecordCode ?? "",
                ServiceDate = CurrentRecord?.ServiceDate.ToString("yyyy-MM-dd") ?? "",
                IsClosed = IsClosed,
                SummaryRows = SummaryRows,
                Totals = new TotalsVm
                {
                    TotalTithes = TotalTithes,
                    TotalOfferings = TotalOfferings,
                    TotalSolomon = TotalSolomon,
                    TotalNoah = TotalNoah,
                    TotalMission = TotalMission,
                    TotalOthers = TotalOthers,
                    GrandTotal = GrandTotal
                },
                Denomination = ToDenominationPayload(denomState)
            };
        }

        private async Task LoadPageDataForRecordAsync(long recordId)
        {
            RecordId = recordId;
            SelectedMemberId = null;
            Input = new GivingInput();
            await LoadPageDataAsync();
        }

        private async Task<DenominationStateVm> BuildDenominationStateAsync(long recordId, decimal grandTotal)
        {
            var response = await _supabase.Client
                .From<GivingDenomination>()
                .Filter("giving_record_id", Operator.Equals, recordId.ToString())
                .Get();

            var entity = response.Models.FirstOrDefault();

            var input = entity == null ? new GivingDenominationInput() : new GivingDenominationInput
            {
                Qty1000 = entity.Qty1000,
                Qty500 = entity.Qty500,
                Qty200 = entity.Qty200,
                Qty100 = entity.Qty100,
                Qty50 = entity.Qty50,
                Qty20Coin = entity.Qty20Coin,
                Qty20Paper = entity.Qty20Paper,
                Qty10 = entity.Qty10,
                Qty5 = entity.Qty5,
                Qty1 = entity.Qty1,
                Qty25Cent = entity.Qty25Cent,
                Qty10Cent = entity.Qty10Cent,
                Qty5Cent = entity.Qty5Cent,
                Qty1Cent = entity.Qty1Cent
            };

            var total = input.ComputeTotal();
            var difference = total - grandTotal;

            return new DenominationStateVm
            {
                Exists = entity != null,
                Input = input,
                Total = total,
                Target = grandTotal,
                Difference = difference,
                IsMatched = grandTotal > 0 && difference == 0
            };
        }

        public async Task<IActionResult> OnPostSaveMemberAjaxAsync([FromBody] SaveMemberAjaxRequest request)
        {
            await _supabase.InitializeAsync(true);

            if (request == null || request.RecordId <= 0)
            {
                return new JsonResult(new AjaxResponse<object>
                {
                    Success = false,
                    Message = "Please load or create a record first.",
                    Data = null
                });
            }

            var hasMember = request.Input.MemberId.HasValue && request.Input.MemberId.Value > 0;
            var hasCustom = !string.IsNullOrWhiteSpace(request.Input.CustomName);

            if (!hasMember && !hasCustom)
            {
                return new JsonResult(new AjaxResponse<object>
                {
                    Success = false,
                    Message = "Select a member or enter a custom name first.",
                    Data = null
                });
            }

            var recordResponse = await _supabase.Client
                .From<GivingRecord>()
                .Filter("id", Operator.Equals, request.RecordId.ToString())
                .Get();

            var currentRecord = recordResponse.Models.FirstOrDefault();

            if (currentRecord == null)
            {
                return new JsonResult(new AjaxResponse<object>
                {
                    Success = false,
                    Message = "Record not found.",
                    Data = null
                });
            }

            if (currentRecord.IsClosed)
            {
                return new JsonResult(new AjaxResponse<object>
                {
                    Success = false,
                    Message = "This record is already finalized.",
                    Data = null
                });
            }

            var drafts = GetDraftEntries(request.RecordId);

            GivingEntryDraftVm? existingDraft = null;

            if (!string.IsNullOrWhiteSpace(request.Input.RowKey))
            {
                existingDraft = drafts.FirstOrDefault(x => x.RowKey == request.Input.RowKey);
            }
            else if (hasMember)
            {
                existingDraft = drafts.FirstOrDefault(x => x.MemberId == request.Input.MemberId);
            }

            if (existingDraft == null)
            {
                existingDraft = new GivingEntryDraftVm
                {
                    RowKey = Guid.NewGuid().ToString("N"),
                    MemberId = request.Input.MemberId,
                    CustomName = hasCustom ? request.Input.CustomName!.Trim() : null,
                    Tithes = request.Input.Tithes,
                    Offerings = request.Input.Offerings,
                    Solomon = request.Input.Solomon,
                    Noah = request.Input.Noah,
                    Mission = request.Input.Mission,
                    Others = request.Input.Others,
                    OthersFund = request.Input.OthersFund
                };

                drafts.Add(existingDraft);
            }
            else
            {
                existingDraft.MemberId = request.Input.MemberId;
                existingDraft.CustomName = hasCustom ? request.Input.CustomName!.Trim() : null;
                existingDraft.Tithes = request.Input.Tithes;
                existingDraft.Offerings = request.Input.Offerings;
                existingDraft.Solomon = request.Input.Solomon;
                existingDraft.Noah = request.Input.Noah;
                existingDraft.Mission = request.Input.Mission;
                existingDraft.Others = request.Input.Others;
                existingDraft.OthersFund = request.Input.OthersFund;
            }

            SaveDraftEntries(request.RecordId, drafts);

            var summaryRows = await BuildSummaryRowsAsync(request.RecordId);
            var totals = BuildTotals(summaryRows);
            var denomState = await BuildDenominationStateAsync(request.RecordId, totals.GrandTotal);

            var savedRow = summaryRows.FirstOrDefault(x => x.RowKey == existingDraft.RowKey);

            var responseData = new SaveMemberAjaxData
            {
                SummaryRows = summaryRows,
                Totals = totals,
                Denomination = ToDenominationPayload(denomState),
                IsClosed = false,
                SavedRow = savedRow
            };

            return new JsonResult(new AjaxResponse<SaveMemberAjaxData>
            {
                Success = true,
                Message = "Row saved to draft.",
                Data = responseData
            });
        }

        public async Task<IActionResult> OnPostFinalizeAjaxAsync([FromBody] FinalizeAjaxRequest request)
        {
            await _supabase.InitializeAsync(true);

            if (request == null || request.RecordId <= 0)
            {
                return new JsonResult(new AjaxResponse<object>
                {
                    Success = false,
                    Message = "Invalid record.",
                    Data = null
                });
            }

            var recordResponse = await _supabase.Client
                .From<GivingRecord>()
                .Filter("id", Operator.Equals, request.RecordId.ToString())
                .Get();

            var record = recordResponse.Models.FirstOrDefault();

            if (record == null)
            {
                return new JsonResult(new AjaxResponse<object>
                {
                    Success = false,
                    Message = "Record not found.",
                    Data = null
                });
            }

            if (record.IsClosed)
            {
                return new JsonResult(new AjaxResponse<object>
                {
                    Success = false,
                    Message = "This record is already finalized.",
                    Data = null
                });
            }

            var drafts = GetDraftEntries(request.RecordId);

            if (!drafts.Any())
            {
                return new JsonResult(new AjaxResponse<object>
                {
                    Success = false,
                    Message = "No entries to finalize.",
                    Data = null
                });
            }

            // --- N+1 FIX: FETCH ALL EXISTING ENTRIES ONCE BEFORE THE LOOP ---
            var existingEntriesResponse = await _supabase.Client
                .From<GivingEntry>()
                .Filter("giving_record_id", Operator.Equals, request.RecordId.ToString())
                .Get();

            var allExistingEntries = existingEntriesResponse.Models;

            foreach (var draft in drafts)
            {
                if (draft.MemberId.HasValue)
                {
                    // Look it up in memory instead of pinging the database again!
                    var existing = allExistingEntries.FirstOrDefault(e => e.MemberId == draft.MemberId.Value);

                    if (existing == null)
                    {
                        await _supabase.Client.From<GivingEntry>().Insert(new GivingEntry
                        {
                            GivingRecordId = request.RecordId,
                            MemberId = draft.MemberId,
                            EntryName = draft.CustomName,
                            Tithes = draft.Tithes,
                            Offerings = draft.Offerings,
                            Solomon = draft.Solomon,
                            Noah = draft.Noah,
                            Mission = draft.Mission,
                            Others = draft.Others,
                            OthersFund = draft.OthersFund,
                            Total = draft.Total,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        });
                    }
                    else
                    {
                        existing.EntryName = draft.CustomName;
                        existing.Tithes = draft.Tithes;
                        existing.Offerings = draft.Offerings;
                        existing.Solomon = draft.Solomon;
                        existing.Noah = draft.Noah;
                        existing.Mission = draft.Mission;
                        existing.Others = draft.Others;
                        existing.OthersFund = draft.OthersFund;
                        existing.Total = draft.Total;
                        existing.UpdatedAt = DateTime.UtcNow;

                        await _supabase.Client.From<GivingEntry>().Update(existing);
                    }
                }
                else
                {
                    await _supabase.Client.From<GivingEntry>().Insert(new GivingEntry
                    {
                        GivingRecordId = request.RecordId,
                        MemberId = null,
                        EntryName = draft.CustomName,
                        Tithes = draft.Tithes,
                        Offerings = draft.Offerings,
                        Solomon = draft.Solomon,
                        Noah = draft.Noah,
                        Mission = draft.Mission,
                        Others = draft.Others,
                        OthersFund = draft.OthersFund,
                        Total = draft.Total,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });
                }
            }

            var refreshedRecordResponse = await _supabase.Client
                .From<GivingRecord>()
                .Filter("id", Operator.Equals, request.RecordId.ToString())
                .Get();

            var refreshedRecord = refreshedRecordResponse.Models.FirstOrDefault();

            if (refreshedRecord == null)
            {
                return new JsonResult(new AjaxResponse<object>
                {
                    Success = false,
                    Message = "Record not found after finalize.",
                    Data = null
                });
            }

            refreshedRecord.RecordCode = string.IsNullOrWhiteSpace(refreshedRecord.RecordCode) ? "CR_01" : refreshedRecord.RecordCode;
            refreshedRecord.ServiceDate = refreshedRecord.ServiceDate.Date.AddHours(12);
            refreshedRecord.IsClosed = true;

            await _supabase.Client.From<GivingRecord>().Update(refreshedRecord);
            ClearDraftEntries(request.RecordId);
            HttpContext.Session.Remove("ActiveGivingRecordId");

            var summaryRows = await BuildSummaryRowsAsync(request.RecordId);
            var totals = BuildTotals(summaryRows);
            var denomState = await BuildDenominationStateAsync(request.RecordId, totals.GrandTotal);

            var responseData = new FinalizeAjaxData
            {
                SummaryRows = summaryRows,
                Totals = totals,
                Denomination = ToDenominationPayload(denomState),
                IsClosed = true
            };

            return new JsonResult(new AjaxResponse<FinalizeAjaxData>
            {
                Success = true,
                Message = "Record finalized successfully.",
                Data = responseData
            });
        }

        public async Task<IActionResult> OnPostLoadRecordAjaxAsync([FromBody] RecordHeaderInput request)
        {
            await _supabase.InitializeAsync(true);

            if (request == null || request.ServiceDate == DateTime.MinValue)
            {
                return new JsonResult(new AjaxResponse<object>
                {
                    Success = false,
                    Message = "Please choose a valid date.",
                    Data = null
                });
            }

            string dateOnly = request.ServiceDate.Date.ToString("yyyy-MM-dd");

            var query = await _supabase.Client
                .From<GivingRecord>()
                .Filter("service_date", Operator.Equals, dateOnly)
                .Get();

            var record = query.Models.FirstOrDefault();

            if (record == null)
            {
                return new JsonResult(new AjaxResponse<object>
                {
                    Success = false,
                    Message = "No existing record found for the selected date.",
                    Data = null
                });
            }

            RecordId = record.Id;
            HttpContext.Session.SetString("ActiveGivingRecordId", RecordId.ToString());
            await LoadPageDataForRecordAsync(record.Id);

            var responseData = await BuildRecordPayloadAsync();

            return new JsonResult(new AjaxResponse<RecordPayloadData>
            {
                Success = true,
                Message = "Record loaded successfully.",
                Data = responseData
            });
        }

        public async Task<IActionResult> OnPostCreateRecordAjaxAsync([FromBody] RecordHeaderInput request)
        {
            await _supabase.InitializeAsync(true);

            if (request == null || request.ServiceDate == DateTime.MinValue)
            {
                return new JsonResult(new AjaxResponse<object>
                {
                    Success = false,
                    Message = "Please choose a valid date.",
                    Data = null
                });
            }

            var safeServiceDate = request.ServiceDate.Date.AddHours(12);
            string dateOnly = safeServiceDate.ToString("yyyy-MM-dd");

            var existingResponse = await _supabase.Client
                .From<GivingRecord>()
                .Filter("service_date", Operator.Equals, dateOnly)
                .Get();

            var existing = existingResponse.Models
                .FirstOrDefault(x => x.RecordCode == request.RecordCode);

            if (existing != null)
            {
                return new JsonResult(new AjaxResponse<object>
                {
                    Success = false,
                    Message = "That record code and date already exist.",
                    Data = null
                });
            }

            var newRecord = new GivingRecord
            {
                RecordCode = string.IsNullOrWhiteSpace(request.RecordCode) ? "CR_01" : request.RecordCode,
                ServiceDate = safeServiceDate,
                IsClosed = false,
                CreatedBy = User.Identity?.Name,
                CreatedAt = DateTime.UtcNow
            };

            try
            {
                var insertResponse = await _supabase.Client
                    .From<GivingRecord>()
                    .Insert(newRecord);

                var inserted = insertResponse.Models.FirstOrDefault();
                var newRecordId = inserted?.Id ?? 0;

                if (newRecordId <= 0)
                {
                    return new JsonResult(new AjaxResponse<object>
                    {
                        Success = false,
                        Message = "Record created but ID was not returned.",
                        Data = null
                    });
                }
                HttpContext.Session.SetString("ActiveGivingRecordId", newRecordId.ToString());

                await LoadPageDataForRecordAsync(newRecordId);

                var responseData = await BuildRecordPayloadAsync();

                return new JsonResult(new AjaxResponse<RecordPayloadData>
                {
                    Success = true,
                    Message = "Record created successfully.",
                    Data = responseData
                });
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("duplicate key") || ex.Message.Contains("already exists"))
                {
                    return new JsonResult(new AjaxResponse<object>
                    {
                        Success = false,
                        Message = "Record already exists for this code/date.",
                        Data = null
                    });
                }

                return new JsonResult(new AjaxResponse<object>
                {
                    Success = false,
                    Message = "Unexpected Error: " + ex.Message,
                    Data = null
                });
            }
        }

        public async Task<IActionResult> OnPostSaveDenominationAjaxAsync([FromBody] SaveDenominationAjaxRequest request)
        {
            await _supabase.InitializeAsync(true);

            if (request == null || request.RecordId <= 0)
            {
                return new JsonResult(new AjaxResponse<object>
                {
                    Success = false,
                    Message = "Invalid record.",
                    Data = null
                });
            }

            var input = request.Denomination ?? new GivingDenominationInput();

            var existingResponse = await _supabase.Client
                .From<GivingDenomination>()
                .Filter("giving_record_id", Operator.Equals, request.RecordId.ToString())
                .Get();

            var existing = existingResponse.Models.FirstOrDefault();

            if (existing == null)
            {
                existing = new GivingDenomination
                {
                    GivingRecordId = request.RecordId,
                    CreatedAt = DateTime.UtcNow
                };

                input.ApplyToEntity(existing);
                await _supabase.Client.From<GivingDenomination>().Insert(existing);
            }
            else
            {
                input.ApplyToEntity(existing);
                await _supabase.Client.From<GivingDenomination>().Update(existing);
            }

            decimal totalTyped = input.ComputeTotal();
            decimal difference = totalTyped - request.TargetTotal;
            bool isMatched = (request.TargetTotal > 0 && difference == 0);

            var responseData = new SaveDenominationAjaxData
            {
                Denomination = new DenominationResultData
                {
                    Total = totalTyped,
                    Difference = difference,
                    IsMatched = isMatched
                },
                Totals = new DenominationTotalsData
                {
                    GrandTotal = request.TargetTotal
                },
                CanFinalize = isMatched && request.TargetTotal > 0
            };

            return new JsonResult(new AjaxResponse<SaveDenominationAjaxData>
            {
                Success = true,
                Message = isMatched ? "Denomination matched." : "Denomination updated.",
                Data = responseData
            });
        }
    }

    public class RecordHeaderInput
    {
        public string RecordCode { get; set; } = "";
        public DateTime ServiceDate { get; set; } = DateTime.Today;
    }

    public class SaveMemberAjaxRequest
    {
        public long RecordId { get; set; }
        public GivingInput Input { get; set; } = new();
    }

    public class FinalizeAjaxRequest
    {
        public long RecordId { get; set; }
    }

    public class SaveDenominationAjaxRequest
    {
        public int RecordId { get; set; }
        public GivingDenominationInput Denomination { get; set; } = new();
        public decimal TargetTotal { get; set; }
    }

    public class GivingInput
    {
        public string? RowKey { get; set; }
        public long? MemberId { get; set; }
        public string? CustomName { get; set; }

        public decimal Tithes { get; set; }
        public decimal Offerings { get; set; }
        public decimal Solomon { get; set; }
        public decimal Noah { get; set; }
        public decimal Mission { get; set; }
        public decimal Others { get; set; }
        public string OthersFund { get; set; } = "General"; // NEW FIELD

        public string DisplayName =>
            !string.IsNullOrWhiteSpace(CustomName) ? CustomName! : "";

        public decimal DisplayTotal =>
            Tithes + Offerings + Solomon + Noah + Mission + Others;
    }

    public class GivingEntryDraftVm
    {
        public string RowKey { get; set; } = Guid.NewGuid().ToString("N");
        public long? MemberId { get; set; }
        public string? CustomName { get; set; }

        public decimal Tithes { get; set; }
        public decimal Offerings { get; set; }
        public decimal Solomon { get; set; }
        public decimal Noah { get; set; }
        public decimal Mission { get; set; }
        public decimal Others { get; set; }
        public string OthersFund { get; set; } = "General"; // NEW FIELD

        public decimal Total =>
            Tithes + Offerings + Solomon + Noah + Mission + Others;
    }

    public class GivingEntryRowVm
    {
        public long Id { get; set; }
        public string RowKey { get; set; } = "";
        public long? MemberId { get; set; }
        public string MemberName { get; set; } = "";
        public string? CustomName { get; set; }

        public decimal Tithes { get; set; }
        public decimal Offerings { get; set; }
        public decimal Solomon { get; set; }
        public decimal Noah { get; set; }
        public decimal Mission { get; set; }
        public decimal Others { get; set; }
        public string OthersFund { get; set; } = "General"; // NEW FIELD
        public decimal Total { get; set; }
    }

    public class TotalsVm
    {
        public decimal TotalTithes { get; set; }
        public decimal TotalOfferings { get; set; }
        public decimal TotalSolomon { get; set; }
        public decimal TotalNoah { get; set; }
        public decimal TotalMission { get; set; }
        public decimal TotalOthers { get; set; }
        public decimal GrandTotal { get; set; }
    }

    public class GivingDenominationInput
    {
        public int Qty1000 { get; set; }
        public int Qty500 { get; set; }
        public int Qty200 { get; set; }
        public int Qty100 { get; set; }
        public int Qty50 { get; set; }
        public int Qty20Coin { get; set; }
        public int Qty20Paper { get; set; }
        public int Qty10 { get; set; }
        public int Qty5 { get; set; }
        public int Qty1 { get; set; }
        public int Qty25Cent { get; set; }
        public int Qty10Cent { get; set; }
        public int Qty5Cent { get; set; }
        public int Qty1Cent { get; set; }

        public decimal ComputeTotal()
        {
            return (Qty1000 * 1000m) +
                   (Qty500 * 500m) +
                   (Qty200 * 200m) +
                   (Qty100 * 100m) +
                   (Qty50 * 50m) +
                   (Qty20Coin * 20m) +
                   (Qty20Paper * 20m) +
                   (Qty10 * 10m) +
                   (Qty5 * 5m) +
                   (Qty1 * 1m) +
                   (Qty25Cent * 0.25m) +
                   (Qty10Cent * 0.10m) +
                   (Qty5Cent * 0.05m) +
                   (Qty1Cent * 0.01m);
        }

        public void ApplyToEntity(GivingDenomination entity)
        {
            entity.Qty1000 = Qty1000;
            entity.Qty500 = Qty500;
            entity.Qty200 = Qty200;
            entity.Qty100 = Qty100;
            entity.Qty50 = Qty50;
            entity.Qty20Coin = Qty20Coin;
            entity.Qty20Paper = Qty20Paper;
            entity.Qty10 = Qty10;
            entity.Qty5 = Qty5;
            entity.Qty1 = Qty1;
            entity.Qty25Cent = Qty25Cent;
            entity.Qty10Cent = Qty10Cent;
            entity.Qty5Cent = Qty5Cent;
            entity.Qty1Cent = Qty1Cent;
            entity.Total = ComputeTotal();
            entity.UpdatedAt = DateTime.UtcNow.AddHours(12);
        }
    }

    public class DenominationStateVm
    {
        public bool Exists { get; set; }
        public GivingDenominationInput Input { get; set; } = new();
        public decimal Total { get; set; }
        public decimal Target { get; set; }
        public decimal Difference { get; set; }
        public bool IsMatched { get; set; }
    }

    public class AjaxResponse<T>
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public T? Data { get; set; }
    }

    public class SaveMemberAjaxData
    {
        public List<GivingEntryRowVm> SummaryRows { get; set; } = new();
        public TotalsVm Totals { get; set; } = new();
        public object? Denomination { get; set; }
        public bool IsClosed { get; set; }
        public GivingEntryRowVm? SavedRow { get; set; }
    }

    public class FinalizeAjaxData
    {
        public List<GivingEntryRowVm> SummaryRows { get; set; } = new();
        public TotalsVm Totals { get; set; } = new();
        public object? Denomination { get; set; }
        public bool IsClosed { get; set; }
    }

    public class LoadRecordAjaxData
    {
        public long RecordId { get; set; }
        public string RecordCode { get; set; } = "";
        public string ServiceDate { get; set; } = "";
        public bool IsClosed { get; set; }

        public List<GivingEntryRowVm> SummaryRows { get; set; } = new();
        public TotalsVm Totals { get; set; } = new();
        public object? Denomination { get; set; }
    }

    public class RecordPayloadData
    {
        public long RecordId { get; set; }
        public string RecordCode { get; set; } = "";
        public string ServiceDate { get; set; } = "";
        public bool IsClosed { get; set; }
        public List<GivingEntryRowVm> SummaryRows { get; set; } = new();
        public TotalsVm Totals { get; set; } = new();
        public object? Denomination { get; set; }
    }

    public class SaveDenominationAjaxData
    {
        public DenominationResultData Denomination { get; set; } = new();
        public DenominationTotalsData Totals { get; set; } = new();
        public bool CanFinalize { get; set; }
    }
    public class DenominationResultData
    {
        public decimal Total { get; set; }
        public decimal Difference { get; set; }
        public bool IsMatched { get; set; }
    }

    public class DenominationTotalsData
    {
        public decimal GrandTotal { get; set; }
    }
}