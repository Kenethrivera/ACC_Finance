using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
using acc_finance.Services;
using acc_finance.Models;
using System.Runtime.InteropServices;

namespace acc_finance.Pages
{
    public class LoginModel : PageModel
    {
        private readonly SupabaseService _supabase;
        private readonly ILogger<LoginModel> _logger;
        
        public LoginModel(SupabaseService supabase, ILogger<LoginModel> logger)
        {
            _supabase = supabase;
            _logger = logger;
        }

        [BindProperty]
        public string Username { get; set; }
        
        [BindProperty]
        public string Password { get; set; }

        public string ErrorMessage { get; set; }

        public void OnGet()
        {
            if (User.Identity.IsAuthenticated)
            {
                Response.Redirect("/Index");
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            await _supabase.InitializeAsync(true);
            var response = await _supabase.Client
                .From<User>()
                .Filter("username", Supabase.Postgrest.Constants.Operator.Equals, Username)
                .Get();

            var user = response.Models.FirstOrDefault();

            if(user == null)
            {
                ErrorMessage = "Invalid Username or Password";
                return Page();
            }

            bool isValidPassword = false;

            if (user.Password.StartsWith("$2"))
            {
                try
                {
                    isValidPassword = BCrypt.Net.BCrypt.Verify(Password, user.Password);
                } catch { isValidPassword = false; }
            }
            else
            {
                if (user.Password == Password)
                {
                    isValidPassword = true;
                }
            }

            if (!isValidPassword)
            {
                ErrorMessage = "Invalid Username or Password";
                await _supabase.Client.From<User>().Update(user);
            }

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role),
                new Claim("FullName", user.Full_Name ?? user.Username)
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            var authProperties = new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
            };

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                authProperties
            );

            _logger.LogInformation(
                "LOGIN SUCCESS: {user} IP:{ip} Device:{device}",
                user.Username,
                HttpContext.Connection.RemoteIpAddress,
                Request.Headers["User-Agent"].ToString()
            );


            return RedirectToPage("/Index");
        }

    }
}
