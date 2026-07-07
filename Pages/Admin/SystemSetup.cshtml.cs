using acc_finance.Models;
using acc_finance.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System;
using static Supabase.Postgrest.Constants;
using static acc_finance.Models.SystemSetup;

namespace acc_finance.Pages.Admin
{
    [Authorize(Roles = "admin,Admin")]
    public class SystemSetupModel : PageModel
    {
        private readonly SupabaseService _supabase;

        public SystemSetupModel(SupabaseService supabase)
        {
            _supabase = supabase;
        }

        [BindProperty(SupportsGet = true)]
        public string SelectedMonth { get; set; } = "";

        // 🚨 FIX: Ironclad backing field. Guarantees no nulls, no capital letters, and always defaults to calendar.
        private string _activeTab = "calendar";
        [BindProperty(SupportsGet = true)]
        public string ActiveTab
        {
            get => string.IsNullOrWhiteSpace(_activeTab) ? "calendar" : _activeTab.ToLower();
            set => _activeTab = value;
        }

        public List<Member> Members { get; set; } = new();
        public List<CalendarDayVm> CalendarDays { get; set; } = new();
        public string CurrentMonthLabel { get; set; } = "";

        public bool IsDetailedExported { get; set; }
        public bool IsFinancialExported { get; set; }

        [BindProperty] public long? EditMemberId { get; set; }
        [BindProperty] public string EditMemberName { get; set; } = "";
        [BindProperty] public bool EditMemberStatus { get; set; }

        [BindProperty] public SystemSettingsRecord AppSettings { get; set; } = new();

        public async Task<IActionResult> OnGetAsync()
        {
            await _supabase.InitializeAsync(true);

            if (string.IsNullOrWhiteSpace(SelectedMonth))
                SelectedMonth = DateTime.Today.ToString("yyyy-MM");

            DateTime firstDay = new DateTime(int.Parse(SelectedMonth.Split('-')[0]), int.Parse(SelectedMonth.Split('-')[1]), 1);
            DateTime lastDay = firstDay.AddMonths(1).AddDays(-1);
            CurrentMonthLabel = firstDay.ToString("MMMM yyyy");

            var membersTask = _supabase.Client.From<Member>().Get();
            var settingsTask = _supabase.Client.From<SystemSettingsRecord>().Filter("id", Operator.Equals, 1).Get();
            var givingTask = _supabase.Client.From<GivingRecord>().Filter("service_date", Operator.GreaterThanOrEqual, firstDay.ToString("yyyy-MM-dd")).Filter("service_date", Operator.LessThanOrEqual, lastDay.ToString("yyyy-MM-dd")).Get();
            var disbTask = _supabase.Client.From<DisbursementRecord>().Filter("record_date", Operator.GreaterThanOrEqual, firstDay.ToString("yyyy-MM-dd")).Filter("record_date", Operator.LessThanOrEqual, lastDay.ToString("yyyy-MM-dd")).Get();
            var exportLogTask = _supabase.Client.From<ExportLogRecord>().Filter("report_month", Operator.Equals, SelectedMonth).Get();

            await Task.WhenAll(membersTask, settingsTask, givingTask, disbTask, exportLogTask);

            var rawMembers = membersTask.Result.Models ?? new List<Member>();
            Members = rawMembers.OrderByDescending(m => m.Is_Active).ThenBy(m => m.Name).ToList();

            AppSettings = settingsTask.Result.Models?.FirstOrDefault() ?? new SystemSettingsRecord();

            var givingRecords = givingTask.Result.Models ?? new List<GivingRecord>();
            var disbRecords = disbTask.Result.Models ?? new List<DisbursementRecord>();
            var exportLog = exportLogTask.Result.Models?.FirstOrDefault();

            decimal totalInc = givingRecords.Sum(g => g.GrandTotal);
            decimal totalDisb = disbRecords.Sum(d => d.TotalReleased - d.TotalReturned);

            string currentDetailedFingerprint = $"DetailedExport_{SelectedMonth}_{totalInc}_{totalDisb}";
            string currentFinFingerprint = $"FinancialExport_{SelectedMonth}_{totalInc}_{totalDisb}";

            IsDetailedExported = exportLog != null && exportLog.DetailedFingerprint == currentDetailedFingerprint;
            IsFinancialExported = exportLog != null && exportLog.FinancialFingerprint == currentFinFingerprint;

            var givingLookup = givingRecords.GroupBy(g => g.ServiceDate.Date).ToDictionary(g => g.Key, g => g.First());
            var disbLookup = disbRecords.GroupBy(d => d.RecordDate.Date).ToDictionary(g => g.Key, g => g.ToList());

            int startDayOfWeek = (int)firstDay.DayOfWeek;
            DateTime gridStart = firstDay.AddDays(-startDayOfWeek);

            for (int i = 0; i < 42; i++)
            {
                DateTime currentDate = gridStart.AddDays(i);
                givingLookup.TryGetValue(currentDate, out var gRec);
                disbLookup.TryGetValue(currentDate, out var dRecs);

                CalendarDays.Add(new CalendarDayVm
                {
                    Date = currentDate,
                    DayNumber = currentDate.Day,
                    IsCurrentMonth = currentDate.Month == firstDay.Month,
                    GivingRecordCode = gRec?.RecordCode,
                    GivingRecordId = gRec?.Id.ToString(),
                    Disbursements = dRecs?.Select(d => new CalendarDisbItem
                    {
                        Id = d.Id,
                        RecordCode = "Disbursement",
                        RecordDateStr = d.RecordDate.ToString("yyyy-MM-dd")
                    }).ToList() ?? new()
                });
            }

            return Page();
        }

        public async Task<IActionResult> OnPostSaveMemberAsync()
        {
            try
            {
                await _supabase.InitializeAsync(true);
                string cleanName = EditMemberName?.Trim() ?? "";

                // Server-side duplicate check (Safety net for backend)
                var duplicateCheck = await _supabase.Client.From<Member>()
                    .Filter("name", Operator.Equals, cleanName)
                    .Get();
                var duplicateUser = duplicateCheck.Models?.FirstOrDefault();

                if (EditMemberId.HasValue && EditMemberId.Value > 0)
                {
                    if (duplicateUser != null && duplicateUser.Id != EditMemberId.Value)
                    {
                        TempData["ErrorMessage"] = $"A member named '{cleanName}' already exists.";
                        return RedirectToPage(new { SelectedMonth, ActiveTab = "members" });
                    }

                    var response = await _supabase.Client.From<Member>()
                        .Filter("id", Operator.Equals, EditMemberId.Value.ToString())
                        .Get();

                    var existingModel = response.Models?.FirstOrDefault();

                    if (existingModel != null)
                    {
                        existingModel.Name = cleanName;
                        existingModel.Is_Active = EditMemberStatus;
                        await existingModel.Update<Member>();
                        TempData["SuccessMessage"] = "Member updated successfully.";
                    }
                }
                else
                {
                    if (duplicateUser != null)
                    {
                        TempData["ErrorMessage"] = $"A member named '{cleanName}' already exists.";
                        return RedirectToPage(new { SelectedMonth, ActiveTab = "members" });
                    }

                    var newMember = new Member
                    {
                        Name = cleanName,
                        Is_Active = EditMemberStatus,
                        CreatedAt = DateTime.UtcNow
                    };
                    await _supabase.Client.From<Member>().Insert(newMember);
                    TempData["SuccessMessage"] = "Member added successfully.";
                }
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "System Error: " + ex.Message;
            }

            return RedirectToPage(new { SelectedMonth, ActiveTab = "members" });
        }

        public async Task<IActionResult> OnPostSaveSettingsAsync()
        {
            try
            {
                await _supabase.InitializeAsync(true);
                AppSettings.Id = 1;
                await _supabase.Client.From<SystemSettingsRecord>().Upsert(AppSettings);
                TempData["SuccessMessage"] = "Settings saved successfully.";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Failed to save settings: " + ex.Message;
            }

            return RedirectToPage(new { SelectedMonth, ActiveTab = "wallets" });
        }
    }

    public class CalendarDisbItem
    {
        public long Id { get; set; }
        public string RecordCode { get; set; } = "";
        public string RecordDateStr { get; set; } = "";
    }

    public class CalendarDayVm
    {
        public DateTime Date { get; set; }
        public int DayNumber { get; set; }
        public bool IsCurrentMonth { get; set; }
        public string? GivingRecordCode { get; set; }
        public string? GivingRecordId { get; set; }
        public List<CalendarDisbItem> Disbursements { get; set; } = new();
    }
}