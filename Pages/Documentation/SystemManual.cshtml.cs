using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace acc_finance.Pages.Documentation
{
    [Authorize]
    public class SystemManualModel : PageModel
    {
        public void OnGet()
        {
            // The System Manual is purely informational. 
            // It utilizes Mermaid.js on the frontend to render the database architecture dynamically.
        }
    }
}