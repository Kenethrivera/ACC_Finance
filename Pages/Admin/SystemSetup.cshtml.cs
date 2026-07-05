using acc_finance.Models;
using acc_finance.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using static acc_finance.Models.SystemSetup;
using static Supabase.Postgrest.Constants;

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

        [BindProperty(SupportsGet = true)]
        public string ActiveTab { get; set; } = "calendar";

        public List<Member> Members { get; set; } = new();
        public List<CalendarDayVm> CalendarDays { get; set; } = new();
        public string CurrentMonthLabel { get; set; }

        // Export Status
        public bool IsDetailedExported { get; set; }
        public bool IsFinancialExported { get; set; }

        // Member Form Bindings
        [BindProperty] public long? EditMemberId { get; set; }
        [BindProperty] public string EditMemberName { get; set; }
        [BindProperty] public bool EditMemberStatus { get; set; }

        // Settings Form Binding
        [BindProperty] public SystemSettingsRecord AppSettings { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            await _supabase.InitializeAsync(true);

            // 1. FETCH MEMBERS 
            var membersResp = await _supabase.Client.From<Member>().Get();
            Members = membersResp.Models.OrderByDescending(m => m.Is_Active).ThenBy(m => m.Name).ToList();

            // 2. FETCH SETTINGS
            var settingsResp = await _supabase.Client.From<SystemSettingsRecord>().Filter("id", Operator.Equals, 1).Get();
            AppSettings = settingsResp.Models.FirstOrDefault() ?? new SystemSettingsRecord();

            // 3. CALENDAR & EXPORT STATUS LOGIC
            if (string.IsNullOrWhiteSpace(SelectedMonth)) SelectedMonth = DateTime.Today.ToString("yyyy-MM");

            DateTime firstDay = new DateTime(int.Parse(SelectedMonth.Split('-')[0]), int.Parse(SelectedMonth.Split('-')[1]), 1);
            DateTime lastDay = firstDay.AddMonths(1).AddDays(-1);
            CurrentMonthLabel = firstDay.ToString("MMMM yyyy");

            var givingResp = await _supabase.Client.From<GivingRecord>()
                .Filter("service_date", Operator.GreaterThanOrEqual, firstDay.ToString("yyyy-MM-dd"))
                .Filter("service_date", Operator.LessThanOrEqual, lastDay.ToString("yyyy-MM-dd")).Get();

            var disbResp = await _supabase.Client.From<DisbursementRecord>()
                .Filter("record_date", Operator.GreaterThanOrEqual, firstDay.ToString("yyyy-MM-dd"))
                .Filter("record_date", Operator.LessThanOrEqual, lastDay.ToString("yyyy-MM-dd")).Get();

            var givingRecords = givingResp.Models ?? new List<GivingRecord>();
            var disbRecords = disbResp.Models ?? new List<DisbursementRecord>();

            // 🚨 Check Permanent DB Export Status 🚨
            decimal totalInc = givingRecords.Sum(g => g.GrandTotal);
            decimal totalDisb = disbRecords.Sum(d => d.TotalReleased - d.TotalReturned);

            string currentDetailedFingerprint = $"DetailedExport_{SelectedMonth}_{totalInc}_{totalDisb}";
            string currentFinFingerprint = $"FinancialExport_{SelectedMonth}_{totalInc}_{totalDisb}";

            var exportLogResp = await _supabase.Client.From<ExportLogRecord>().Filter("report_month", Operator.Equals, SelectedMonth).Get();
            var exportLog = exportLogResp.Models.FirstOrDefault();

            IsDetailedExported = exportLog != null && exportLog.DetailedFingerprint == currentDetailedFingerprint;
            IsFinancialExported = exportLog != null && exportLog.FinancialFingerprint == currentFinFingerprint;

            // Build Calendar Grid
            int startDayOfWeek = (int)firstDay.DayOfWeek;
            DateTime gridStart = firstDay.AddDays(-startDayOfWeek);

            for (int i = 0; i < 42; i++)
            {
                DateTime currentDate = gridStart.AddDays(i);
                var gRec = givingRecords.FirstOrDefault(g => g.ServiceDate.Date == currentDate.Date);
                int dRecCount = disbRecords.Count(d => d.RecordDate.Date == currentDate.Date);

                CalendarDays.Add(new CalendarDayVm
                {
                    Date = currentDate,
                    DayNumber = currentDate.Day,
                    IsCurrentMonth = currentDate.Month == firstDay.Month,
                    GivingRecordCode = gRec?.RecordCode,
                    GivingRecordId = gRec?.Id.ToString(),
                    DisbursementCount = dRecCount
                });
            }

            return Page();
        }

        public async Task<IActionResult> OnPostSaveMemberAsync()
        {
            await _supabase.InitializeAsync(true);

            var member = new Member
            {
                Name = EditMemberName,
                Is_Active = EditMemberStatus,
            };

            if (EditMemberId.HasValue && EditMemberId.Value > 0)
            {
                member.Id = EditMemberId.Value;
                await _supabase.Client.From<Member>().Update(member);
            }
            else
            {
                member.CreatedAt = DateTime.UtcNow;
                await _supabase.Client.From<Member>().Insert(member);
            }

            return RedirectToPage(new { SelectedMonth, ActiveTab = "members" });
        }

        public async Task<IActionResult> OnPostSaveSettingsAsync()
        {
            await _supabase.InitializeAsync(true);

            AppSettings.Id = 1;
            await _supabase.Client.From<SystemSettingsRecord>().Upsert(AppSettings);

            return RedirectToPage(new { SelectedMonth, ActiveTab = "wallets" });
        }
    }

    public class CalendarDayVm
    {
        public DateTime Date { get; set; }
        public int DayNumber { get; set; }
        public bool IsCurrentMonth { get; set; }
        public string GivingRecordCode { get; set; }
        public string GivingRecordId { get; set; }
        public int DisbursementCount { get; set; }
    }

    
}