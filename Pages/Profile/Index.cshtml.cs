using acc_finance.Models;
using acc_finance.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using static Supabase.Postgrest.Constants;

namespace acc_finance.Pages.Profile
{
    [Authorize]
    public class IndexModel : PageModel
    {
        private readonly SupabaseService _supabase;
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(SupabaseService supabase, ILogger<IndexModel> logger)
        {
            _supabase = supabase;
            _logger = logger;
        }

        [BindProperty]
        public ProfileUpdateInput ProfileInput { get; set; } = new();

        [BindProperty]
        public PasswordUpdateInput PasswordInput { get; set; } = new();

        [TempData]
        public string SuccessMessage { get; set; }

        [TempData]
        public string ErrorMessage { get; set; }

        public string CurrentRole { get; set; } = "";
        public string UserInitials { get; set; } = "U";

        public IActionResult OnGetAsync()
        {
            LoadUserData();
            return Page();
        }

        // ==========================================
        // HANDLER 1: UPDATE PROFILE (NAME & USERNAME)
        // ==========================================
        public async Task<IActionResult> OnPostUpdateProfileAsync()
        {
            // 🚨 BULLETPROOF MULTI-FORM FIX 🚨
            ModelState.Clear(); // Wipe all implicit errors from the other form
            if (!TryValidateModel(ProfileInput, nameof(ProfileInput))) // Validate ONLY the Profile
            {
                LoadUserData();
                return Page();
            }

            await _supabase.InitializeAsync(true);
            var currentUsername = User.Identity?.Name;

            var userResponse = await _supabase.Client
                .From<User>()
                .Filter("username", Operator.Equals, currentUsername)
                .Get();

            var user = userResponse.Models.FirstOrDefault();

            if (user == null)
            {
                ErrorMessage = "System error: Could not locate your user profile.";
                return RedirectToPage("/Logout");
            }

            if (!string.Equals(user.Username, ProfileInput.Username, StringComparison.OrdinalIgnoreCase))
            {
                var duplicateCheck = await _supabase.Client
                    .From<User>()
                    .Filter("username", Operator.Equals, ProfileInput.Username.Trim())
                    .Get();

                if (duplicateCheck.Models.Any())
                {
                    ErrorMessage = "That username is already taken. Please choose another one.";
                    LoadUserData();
                    return Page();
                }
            }

            // Update Database
            user.Full_Name = ProfileInput.FullName.Trim();
            user.Username = ProfileInput.Username.Trim();
            await _supabase.Client.From<User>().Update(user);

            // Refresh Authentication Cookie silently
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role),
                new Claim("FullName", user.Full_Name)
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

            SuccessMessage = "Profile updated successfully!";
            return RedirectToPage();
        }

        // ==========================================
        // HANDLER 2: CHANGE PASSWORD
        // ==========================================
        public async Task<IActionResult> OnPostChangePasswordAsync()
        {
            // 🚨 BULLETPROOF MULTI-FORM FIX 🚨
            ModelState.Clear(); // Wipe all implicit errors from the other form
            if (!TryValidateModel(PasswordInput, nameof(PasswordInput))) // Validate ONLY the Password
            {
                LoadUserData();
                return Page();
            }

            if (PasswordInput.NewPassword != PasswordInput.ConfirmPassword)
            {
                ErrorMessage = "New passwords do not match.";
                LoadUserData();
                return Page();
            }

            await _supabase.InitializeAsync(true);
            var currentUsername = User.Identity?.Name;

            var userResponse = await _supabase.Client
                .From<User>()
                .Filter("username", Operator.Equals, currentUsername)
                .Get();

            var user = userResponse.Models.FirstOrDefault();

            if (user == null)
            {
                ErrorMessage = "System error: Could not locate your user profile.";
                return Page();
            }

            bool isCurrentPasswordValid = false;
            if (user.Password.StartsWith("$2"))
            {
                try { isCurrentPasswordValid = BCrypt.Net.BCrypt.Verify(PasswordInput.CurrentPassword, user.Password); }
                catch { isCurrentPasswordValid = false; }
            }
            else
            {
                isCurrentPasswordValid = (user.Password == PasswordInput.CurrentPassword);
            }

            if (!isCurrentPasswordValid)
            {
                ErrorMessage = "Incorrect current password.";
                LoadUserData();
                return Page();
            }

            // Encrypt and Save
            string hashedNewPassword = BCrypt.Net.BCrypt.HashPassword(PasswordInput.NewPassword);
            user.Password = hashedNewPassword;

            await _supabase.Client.From<User>().Update(user);

            _logger.LogInformation("SECURITY: Password changed successfully for user {User}", user.Username);

            SuccessMessage = "Password changed successfully!";
            return RedirectToPage();
        }

        private void LoadUserData()
        {
            var fullName = User.FindFirst("FullName")?.Value ?? "User";
            CurrentRole = User.FindFirst(ClaimTypes.Role)?.Value ?? "Unknown Role";
            UserInitials = fullName.Substring(0, 1).ToUpper();

            if (string.IsNullOrEmpty(ProfileInput.Username))
            {
                ProfileInput.FullName = fullName;
                ProfileInput.Username = User.Identity?.Name ?? "";
            }
        }
    }

    public class ProfileUpdateInput
    {
        [Required(ErrorMessage = "Full Name is required.")]
        public string FullName { get; set; } = "";

        [Required(ErrorMessage = "Username is required.")]
        [MinLength(4, ErrorMessage = "Username must be at least 4 characters long.")]
        public string Username { get; set; } = "";
    }

    public class PasswordUpdateInput
    {
        [Required(ErrorMessage = "Current Password is required.")]
        public string CurrentPassword { get; set; } = "";

        [Required(ErrorMessage = "New Password is required.")]
        [MinLength(8, ErrorMessage = "New password must be at least 8 characters long.")]
        public string NewPassword { get; set; } = "";

        [Required(ErrorMessage = "Please confirm your new password.")]
        public string ConfirmPassword { get; set; } = "";
    }
}