using Microsoft.AspNetCore.Mvc;
using acc_finance.Services;

namespace acc_finance.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AiController : ControllerBase
    {
        private readonly AiAssistantService _aiService;

        public AiController(AiAssistantService aiService)
        {
            _aiService = aiService;
        }

        [HttpPost("ask")]
        public async Task<IActionResult> AskAssistant([FromForm] string userQuestion)
        {
            // 1. Send the question to our new service
            string aiResponseText = await _aiService.ProcessUserQuestionAsync(userQuestion);

            // 2. Wrap the response in your HTML bubble
            string htmlResponse = $@"
                <div class='msg-bubble msg-ai'>
                    {aiResponseText}
                </div>
            ";

            return Content(htmlResponse, "text/html");
        }
    }
}