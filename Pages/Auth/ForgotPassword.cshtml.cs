using acc_finance.Models;
using acc_finance.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using static Supabase.Postgrest.Constants;
using Microsoft.AspNetCore.Authorization;

namespace acc_finance.Pages.Auth
{
    [AllowAnonymous]
    public class ForgotPasswordModel : PageModel
    {
        private readonly SupabaseService _supabase;
        private readonly SecurityQuestionService _questionService;

        public ForgotPasswordModel(SupabaseService supabase, SecurityQuestionService questionService)
        {
            _supabase = supabase;
            _questionService = questionService;
        }

        // State Machine Properties
        public int CurrentStep { get; set; } = 1;
        public int AttemptsLeft { get; set; } = 3;
        public bool CanSkip { get; set; } = true;
        public SecurityQuestion CurrentQuestion { get; set; } = new();

        [TempData]
        public string ErrorMessage { get; set; }
        [TempData]
        public string SuccessMessage { get; set; }

        [BindProperty]
        public string UsernameInput { get; set; } = "";

        [BindProperty]
        public string AnswerInput { get; set; } = "";

        [BindProperty]
        public PasswordResetInput ResetInput { get; set; } = new();

        public void OnGet()
        {
            // 🚨 THE FIX: Only clear the session if the user is starting fresh!
            // If they are redirected here from Step 1, TempData protects the session.
            if (TempData["FlowActive"] == null)
            {
                HttpContext.Session.Remove("FP_Username");
                HttpContext.Session.Remove("FP_Step");
                HttpContext.Session.Remove("FP_Attempts");
                HttpContext.Session.Remove("FP_CanSkip");
                HttpContext.Session.Remove("FP_QuestionId");
            }
            else
            {
                TempData.Keep("FlowActive"); // Keep it alive in case they hit Refresh
            }
        }

        public async Task<IActionResult> OnPostVerifyUsernameAsync()
        {
            await _supabase.InitializeAsync(true);

            if (string.IsNullOrWhiteSpace(UsernameInput))
            {
                ErrorMessage = "Please enter your username.";
                return Page();
            }

            var response = await _supabase.Client
                .From<User>()
                .Filter("username", Operator.Equals, UsernameInput.Trim())
                .Get();

            var user = response.Models.FirstOrDefault();

            if (user == null)
            {
                ErrorMessage = "Username not found.";
                return Page();
            }

            // Setup Session for Step 2
            HttpContext.Session.SetString("FP_Username", user.Username);
            HttpContext.Session.SetInt32("FP_Step", 2);
            HttpContext.Session.SetInt32("FP_Attempts", 3);
            HttpContext.Session.SetInt32("FP_CanSkip", 1);

            var q = _questionService.GetRandomQuestion();
            HttpContext.Session.SetInt32("FP_QuestionId", q.Id);

            // 🚨 THE FIX: Protect the session during the redirect!
            TempData["FlowActive"] = true;
            return RedirectToPage();
        }

        public IActionResult OnPostSkipQuestion()
        {
            if (!CanSkip)
            {
                ErrorMessage = "You have already used your skip.";
                return Page();
            }

            HttpContext.Session.SetInt32("FP_CanSkip", 0);

            var newQ = _questionService.GetRandomQuestion(CurrentQuestion.Id);
            HttpContext.Session.SetInt32("FP_QuestionId", newQ.Id);
            HttpContext.Session.SetInt32("FP_Attempts", 3);

            SuccessMessage = "Question switched. You have 3 attempts for this new question.";

            TempData["FlowActive"] = true;
            return RedirectToPage();
        }

        public IActionResult OnPostSubmitAnswer()
        {
            if (CurrentStep != 2) return RedirectToPage("/Index");

            bool isCorrect = _questionService.CheckAnswer(CurrentQuestion.Id, AnswerInput);

            if (isCorrect)
            {
                HttpContext.Session.SetInt32("FP_Step", 3);
                SuccessMessage = "Identity verified! Please create a new password.";

                TempData["FlowActive"] = true;
                return RedirectToPage();
            }
            else
            {
                AttemptsLeft--;
                HttpContext.Session.SetInt32("FP_Attempts", AttemptsLeft);

                if (AttemptsLeft <= 0)
                {
                    HttpContext.Session.Clear();
                    TempData["ErrorMessage"] = "Too many failed attempts. Security lockout triggered.";
                    return RedirectToPage("/Login");
                }

                ErrorMessage = $"Incorrect answer. You have {AttemptsLeft} attempt(s) left.";
                return Page();
            }
        }

        public async Task<IActionResult> OnPostResetPasswordAsync()
        {
            if (CurrentStep != 3) return RedirectToPage("/Index");

            // 🚨 BULLETPROOF VALIDATION FIX 🚨
            ModelState.Clear(); // Wipe any implicit errors from Step 1 and Step 2 fields
            if (!TryValidateModel(ResetInput, nameof(ResetInput)) || ResetInput.NewPassword != ResetInput.ConfirmPassword)
            {
                ErrorMessage = "Passwords do not match or do not meet requirements.";
                return Page();
            }

            // ==========================================================
            // 🚨 THIS IS THE BOTTOM HALF THAT ACCIDENTALLY GOT DELETED 🚨
            // ==========================================================

            await _supabase.InitializeAsync(true);
            string savedUsername = HttpContext.Session.GetString("FP_Username") ?? "";

            var userResponse = await _supabase.Client
                .From<User>()
                .Filter("username", Operator.Equals, savedUsername)
                .Get();

            var user = userResponse.Models.FirstOrDefault();

            if (user != null)
            {
                string hashedNewPassword = BCrypt.Net.BCrypt.HashPassword(ResetInput.NewPassword);
                user.Password = hashedNewPassword;
                await _supabase.Client.From<User>().Update(user);
            }

            HttpContext.Session.Clear();
            TempData["SuccessMessage"] = "Password successfully reset! You may now log in.";
            return RedirectToPage("/Login");
        }

        // Automatically load session state for the UI before ANY handler runs
        public override async Task OnPageHandlerExecutionAsync(Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutingContext context, Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutionDelegate next)
        {
            CurrentStep = HttpContext.Session.GetInt32("FP_Step") ?? 1;

            if (CurrentStep >= 2)
            {
                AttemptsLeft = HttpContext.Session.GetInt32("FP_Attempts") ?? 3;
                CanSkip = (HttpContext.Session.GetInt32("FP_CanSkip") ?? 1) == 1;
                UsernameInput = HttpContext.Session.GetString("FP_Username") ?? "";

                int qId = HttpContext.Session.GetInt32("FP_QuestionId") ?? 1;
                CurrentQuestion = _questionService.GetQuestionById(qId) ?? new SecurityQuestion();
            }

            await next();
        }
    }

    public class PasswordResetInput
    {
        [Required]
        [MinLength(8, ErrorMessage = "Password must be at least 8 characters.")]
        public string NewPassword { get; set; } = "";
        [Required]
        public string ConfirmPassword { get; set; } = "";
    }
}