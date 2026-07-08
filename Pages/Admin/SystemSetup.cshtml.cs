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
using System.Globalization;

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

        [BindProperty] public string EditWalletTheme { get; set; } = "primary";
        [BindProperty] public string EditWalletIcon { get; set; } = "bi-wallet2";

        // 2. Add the Initial Balance property (It is NOT part of the Wallet table, it's just for the form)
        [BindProperty] public decimal EditWalletInitialBalance { get; set; } = 0;

        // --- WALLETS & MINISTRIES ---
        public List<Wallet> Wallets { get; set; } = new();
        public List<Ministry> Ministries { get; set; } = new();

        [BindProperty] public long? EditWalletId { get; set; }
        [BindProperty] public string EditWalletCode { get; set; } = "";
        [BindProperty] public string EditWalletDisplayName { get; set; } = "";
        [BindProperty] public string EditWalletCustodianType { get; set; } = "Person";
        [BindProperty] public string EditWalletCustodianPersonName { get; set; } = "";
        [BindProperty] public long? EditWalletMinistryId { get; set; }
        [BindProperty] public int EditWalletSortOrder { get; set; } = 0;
        [BindProperty] public bool EditWalletIsActive { get; set; } = true;

        [BindProperty] public long? EditMinistryId { get; set; }
        [BindProperty] public string EditMinistryName { get; set; } = "";
        [BindProperty] public string EditMinistryLeaderName { get; set; } = "";
        [BindProperty] public string EditMinistryContactInfo { get; set; } = "";
        [BindProperty] public bool EditMinistryIsActive { get; set; } = true;

        public Dictionary<string, decimal> WalletInitialBalances { get; set; } = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

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
            var walletsTask = _supabase.Client.From<Wallet>().Get();
            var ministriesTask = _supabase.Client.From<Ministry>().Get();

            await Task.WhenAll(membersTask, settingsTask, givingTask, disbTask, exportLogTask, walletsTask, ministriesTask);

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

            // --- WALLETS & MINISTRIES ---
            var rawWallets = walletsTask.Result.Models ?? new List<Wallet>();
            Wallets = rawWallets.OrderByDescending(w => w.Is_Active).ThenBy(w => w.Id).ToList();

            // Fetch all balances
            var balancesResp = await _supabase.Client.From<MonthlyFundBalance>().Get();
            var allBalances = balancesResp.Models ?? new List<MonthlyFundBalance>();

            foreach (var w in Wallets)
            {
                var initialBal = allBalances
                    .Where(b => string.Equals(b.FundName, w.Code, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(b => b.ReportMonth)
                    .FirstOrDefault();

                // Save to dictionary
                WalletInitialBalances[w.Code] = initialBal?.EndingBalance ?? 0m;
            }

            var rawMinistries = ministriesTask.Result.Models ?? new List<Ministry>();
            Ministries = rawMinistries.OrderByDescending(m => m.Is_Active).ThenBy(m => m.Name).ToList();

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

            return RedirectToPage(new { SelectedMonth, ActiveTab = "integrations" });
        }

        // --- WALLET HANDLERS ---

        public async Task<IActionResult> OnPostSaveWalletAsync()
        {
            try
            {
                await _supabase.InitializeAsync(true);

                string cleanDisplayName = EditWalletDisplayName?.Trim() ?? "";
                string cleanCode = EditWalletCode?.Trim() ?? "";
                string cleanPersonName = EditWalletCustodianPersonName?.Trim() ?? "";

                if (string.IsNullOrWhiteSpace(cleanDisplayName) || string.IsNullOrWhiteSpace(cleanCode))
                {
                    TempData["ErrorMessage"] = "Wallet name and code are required.";
                    return RedirectToPage(new { SelectedMonth, ActiveTab = "wallets" });
                }

                if (EditWalletId.HasValue && EditWalletId.Value > 0)
                {
                    // --- EDIT EXISTING WALLET ---
                    var response = await _supabase.Client.From<Wallet>()
                        .Filter("id", Operator.Equals, EditWalletId.Value.ToString())
                        .Get();

                    var existingModel = response.Models?.FirstOrDefault();

                    if (existingModel != null)
                    {
                        existingModel.DisplayName = cleanDisplayName;
                        existingModel.CustodianType = EditWalletCustodianType;
                        existingModel.CustodianPersonName = EditWalletCustodianType == "Person" ? cleanPersonName : null;
                        existingModel.MinistryId = EditWalletCustodianType == "Ministry" ? EditWalletMinistryId : null;
                        existingModel.Theme = EditWalletTheme;
                        existingModel.Icon = EditWalletIcon;
                        existingModel.Is_Active = EditWalletIsActive;

                        await existingModel.Update<Wallet>();
                        TempData["SuccessMessage"] = "Wallet updated successfully.";
                    }
                }
                else
                {
                    // --- CREATE NEW WALLET ---
                    var duplicateCheck = await _supabase.Client.From<Wallet>()
                        .Filter("code", Operator.Equals, cleanCode)
                        .Get();

                    if (duplicateCheck.Models?.Any() == true)
                    {
                        TempData["ErrorMessage"] = $"A wallet with code '{cleanCode}' already exists.";
                        return RedirectToPage(new { SelectedMonth, ActiveTab = "wallets" });
                    }

                    var newWallet = new Wallet
                    {
                        Code = cleanCode,
                        DisplayName = cleanDisplayName,
                        CustodianType = EditWalletCustodianType,
                        CustodianPersonName = EditWalletCustodianType == "Person" ? cleanPersonName : null,
                        MinistryId = EditWalletCustodianType == "Ministry" ? EditWalletMinistryId : null,
                        Theme = EditWalletTheme,
                        Icon = EditWalletIcon,
                        Is_Active = EditWalletIsActive,
                        CreatedAt = DateTime.UtcNow
                    };

                    await _supabase.Client.From<Wallet>().Insert(newWallet);

                    // --- 🚨 INITIAL BALANCE LOGIC 🚨 ---
                    // If the user entered a starting balance > 0, we create a snapshot for it.
                    if (EditWalletInitialBalance > 0)
                    {
                        // Safety check: Fallback to current month if SelectedMonth is null
                        string targetMonth = string.IsNullOrEmpty(SelectedMonth) ? DateTime.Today.ToString("yyyy-MM") : SelectedMonth;

                        // Calculate the PREVIOUS month based on SelectedMonth (e.g., if SelectedMonth is 2026-07, target is 2026-06)
                        // This ensures the system sees it as a carry-over balance for the current month.
                        DateTime currentSelection = DateTime.ParseExact(targetMonth + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
                        string previousMonthStr = currentSelection.AddMonths(-1).ToString("yyyy-MM");

                        // Determine the custodian name for the record
                        string custodianName = EditWalletCustodianType == "Person"
                            ? cleanPersonName
                            : "Ministry Wallet"; // Fallback text if it's assigned to a Ministry

                        var initialBalanceRecord = new MonthlyFundBalance
                        {
                            FundName = cleanCode,
                            ReportMonth = previousMonthStr,
                            EndingBalance = EditWalletInitialBalance, // Matches your C# Model exactly
                            Custodian = custodianName,                // Added to match your C# Model
                            CreatedAt = DateTime.UtcNow
                        };

                        await _supabase.Client.From<MonthlyFundBalance>().Insert(initialBalanceRecord);
                    }

                    TempData["SuccessMessage"] = "Wallet created successfully with initial balance.";
                }
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "System Error: " + ex.Message;
            }

            return RedirectToPage(new { SelectedMonth, ActiveTab = "wallets" });
        }

        // --- MINISTRY HANDLERS ---

        public async Task<IActionResult> OnPostSaveMinistryAsync()
        {
            try
            {
                await _supabase.InitializeAsync(true);
                string cleanName = EditMinistryName?.Trim() ?? "";

                if (string.IsNullOrWhiteSpace(cleanName))
                {
                    TempData["ErrorMessage"] = "Ministry name is required.";
                    return RedirectToPage(new { SelectedMonth, ActiveTab = "wallets" });
                }

                var duplicateCheck = await _supabase.Client.From<Ministry>()
                    .Filter("name", Operator.Equals, cleanName)
                    .Get();
                var duplicate = duplicateCheck.Models?.FirstOrDefault();

                if (EditMinistryId.HasValue && EditMinistryId.Value > 0)
                {
                    if (duplicate != null && duplicate.Id != EditMinistryId.Value)
                    {
                        TempData["ErrorMessage"] = $"A ministry named '{cleanName}' already exists.";
                        return RedirectToPage(new { SelectedMonth, ActiveTab = "wallets" });
                    }

                    var response = await _supabase.Client.From<Ministry>()
                        .Filter("id", Operator.Equals, EditMinistryId.Value.ToString())
                        .Get();

                    var existingModel = response.Models?.FirstOrDefault();
                    if (existingModel != null)
                    {
                        existingModel.Name = cleanName;
                        existingModel.LeaderName = string.IsNullOrWhiteSpace(EditMinistryLeaderName) ? null : EditMinistryLeaderName.Trim();
                        existingModel.ContactInfo = string.IsNullOrWhiteSpace(EditMinistryContactInfo) ? null : EditMinistryContactInfo.Trim();
                        existingModel.Is_Active = EditMinistryIsActive;

                        await existingModel.Update<Ministry>();
                        TempData["SuccessMessage"] = "Ministry updated successfully.";
                    }
                }
                else
                {
                    if (duplicate != null)
                    {
                        TempData["ErrorMessage"] = $"A ministry named '{cleanName}' already exists.";
                        return RedirectToPage(new { SelectedMonth, ActiveTab = "wallets" });
                    }

                    var newMinistry = new Ministry
                    {
                        Name = cleanName,
                        LeaderName = string.IsNullOrWhiteSpace(EditMinistryLeaderName) ? null : EditMinistryLeaderName.Trim(),
                        ContactInfo = string.IsNullOrWhiteSpace(EditMinistryContactInfo) ? null : EditMinistryContactInfo.Trim(),
                        Is_Active = EditMinistryIsActive,
                        CreatedAt = DateTime.UtcNow
                    };

                    await _supabase.Client.From<Ministry>().Insert(newMinistry);
                    TempData["SuccessMessage"] = "Ministry added successfully.";
                }
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "System Error: " + ex.Message;
            }

            return RedirectToPage(new { SelectedMonth, ActiveTab = "wallets" });
        }

        public async Task<IActionResult> OnPostDeleteWalletAsync(long id, string activeTab, string selectedMonth)
        {
            try
            {
                await _supabase.InitializeAsync(true);

                // Execute the delete command
                await _supabase.Client.From<Wallet>()
                    .Filter("id", Operator.Equals, id.ToString())
                    .Delete();

                TempData["SuccessMessage"] = "Wallet deleted successfully.";
            }
            catch (Exception ex)
            {
                // If a wallet has transactions tied to it, the database will block the deletion (Foreign Key Constraint).
                TempData["ErrorMessage"] = "Cannot delete wallet. It likely has existing transactions tied to it. Disable it instead. Error: " + ex.Message;
            }

            return RedirectToPage(new { SelectedMonth = selectedMonth, ActiveTab = activeTab });
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