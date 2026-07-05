using acc_finance.Models;
using System.Text;
using System.Text.Json;

namespace acc_finance.Services
{
    public class AiAuditorService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _config;

        public AiAuditorService(HttpClient httpClient, IConfiguration config)
        {
            _config = config;
            _httpClient = httpClient;
        }

        public async Task<string> GenerateAuditSummaryAsync(VerificationLedger ledgerData)
        {
            var jsonData = JsonSerializer.Serialize(ledgerData);

            var prompt = $@"
        You are an expert financial auditor. Review the following sanitized JSON ledger. 
        The system has ALREADY calculated the math perfectly. DO NOT perform any addition or subtraction yourself.

        INSTRUCTIONS:
        - Output your response entirely in clean HTML. Use Bootstrap 5 classes.
        - For headers, use a styled div that matches the blue alert style.

        Structure:

        <div class='p-2 mb-2 fw-bold text-primary border-bottom border-primary border-2' style='font-size: 1.1rem;'>
            1. DAILY BLESSINGS & WALLET ALLOCATIONS
        </div>
        <table class='table table-sm align-middle shadow-sm mb-4' style='border: 1px solid #e2e8f0;'>
            <thead style='background-color: #f8fafc; color: #475569; font-size: 0.75rem;'>
                <tr><th>DATE</th><th>TOTAL BLESSING</th><th>WALLET ALLOCATIONS</th><th class='text-center'>AUDIT CHECK</th></tr>
            </thead>
            <tbody style='font-size: 0.85rem;'>
                (Loop through DailySummaries: Create rows. Format Wallet Allocations using <span class='badge bg-light text-dark border'>. 
                For the AUDIT CHECK column, output EXACTLY this HTML, replacing [DateId] with the exact DateId from the JSON:
                <td class='text-center'>
                    <div class='fw-bold text-success mb-1'><i class='bi bi-check-circle-fill'></i> Verified</div>
                    <span class='badge bg-white text-primary border border-primary audit-hover-trigger' style='cursor:pointer;' data-target='giving-[DateId]'><i class='bi bi-search'></i> Preview</span>
                </td>)
            </tbody>
        </table>

        <div class='p-2 mb-2 fw-bold text-primary border-bottom border-primary border-2' style='font-size: 1.1rem;'>
            2. DAILY DISBURSEMENTS & RETURNS
        </div>
        <table class='table table-sm align-middle shadow-sm mb-4' style='border: 1px solid #e2e8f0;'>
            <thead style='background-color: #f8fafc; color: #475569; font-size: 0.75rem;'>
                <tr><th>DATE</th><th>DISBURSED</th><th>RETURNED</th><th>ADJUSTED</th><th>FUND SOURCE</th><th class='text-center'>AUDIT CHECK</th></tr>
            </thead>
            <tbody style='font-size: 0.85rem;'>
                (Loop through DailySummaries: Create rows. For the AUDIT CHECK column, output EXACTLY this HTML, replacing [DateId] with the exact DateId:
                <td class='text-center'>
                    <div class='fw-bold text-success mb-1'><i class='bi bi-check-circle-fill'></i> Verified</div>
                    <span class='badge bg-white text-primary border border-primary audit-hover-trigger' style='cursor:pointer;' data-target='disb-[DateId]'><i class='bi bi-search'></i> Preview</span>
                </td>)
            </tbody>
        </table>

        <div class='p-4 mb-4 rounded-3 shadow-sm' style='background-color: #f0f9ff; border: 1px solid #3b82f6;'>
            <div class='d-flex justify-content-between align-items-center border-bottom border-primary border-opacity-25 pb-2 mb-3'>
                <h6 class='fw-bold text-primary mb-0'>3. OVERALL NET COMPUTATION</h6>
                <span class='fw-bold text-success'><i class='bi bi-check-circle-fill'></i> Verified</span>
            </div>
            
            <div class='mx-auto' style='max-width: 450px; font-size: 0.95rem; color: #334155;'>
                <div class='d-flex justify-content-between py-1'>
                    <span>Beginning Balance (<span class='text-muted' style='font-size: 0.85rem;'>from [ReportMonth]</span>)</span>
                    <span>[BeginningBalance]</span>
                </div>
                <div class='d-flex justify-content-between py-1'>
                    <span>Add: Cash Receipts & Blessings</span>
                    <span>[TotalGiving]</span>
                </div>
                <div class='d-flex justify-content-between py-1 border-bottom border-secondary pb-2 mb-2'>
                    <span>Less: Total Disbursements</span>
                    <span>[TotalAdjustedDisbursement]</span>
                </div>
                <div class='d-flex justify-content-between fw-bold text-dark fs-5 mt-1'>
                    <span>NET CASH BALANCE</span>
                    <span>[NetCashBalance]</span>
                </div>
            </div>
        </div>

        <div class='p-2 mb-2 fw-bold text-primary border-bottom border-primary border-2' style='font-size: 1.1rem;'>
            4. WALLET / FUND HEALTH
        </div>
        <table class='table table-sm align-middle shadow-sm mb-4' style='border: 1px solid #e2e8f0;'>
            <thead style='background-color: #f8fafc; color: #475569; font-size: 0.75rem;'>
                <tr><th>FUND NAME</th><th>BEG. BALANCE</th><th>INCOME</th><th>DISBURSED</th><th>ENDING</th><th class='text-center'>AUDIT CHECK</th></tr>
            </thead>
            <tbody style='font-size: 0.85rem;'>
                (Loop through FundAudits. Output simple text. If 'IsInDeficit' is true, style ending balance with text-danger and add a <br><span class='badge bg-danger'>Deficit</span> warning. 
                For the AUDIT CHECK column, output EXACTLY this HTML:
                <td class='text-center'>
                    <div class='fw-bold text-success mb-1'><i class='bi bi-check-circle-fill'></i> Verified</div>
                    <span class='badge bg-white text-primary border border-primary audit-hover-trigger' style='cursor:pointer;' data-target='wallet-dashboard'><i class='bi bi-search'></i> Preview</span>
                </td>)
            </tbody>
        </table>

        <div class='alert alert-secondary border-0 shadow-sm text-center fw-bold py-2' style='background-color: #f1f5f9;'>
            Based on this audit, your dashboard should display: Book Balance: <span class='text-primary'>[BookBalance]</span> | Cash on Hand: <span class='text-primary'>[CashOnHand]</span>
        </div>

        DATA: {jsonData}";

            var requestBody = new { contents = new[] { new { parts = new[] { new { text = prompt } } } } };
            var jsonContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            var apiKey = _config["Gemini:ApiKey"];
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-flash-latest:generateContent?key={apiKey}";

            var response = await _httpClient.PostAsync(url, jsonContent);

            if (!response.IsSuccessStatusCode)
            {
                var errorDetails = await response.Content.ReadAsStringAsync();
                return $"Error: Unable to reach the AI Auditor. Details: {errorDetails}";
            }

            var responseString = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(responseString);

            return document.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "No summary generated.";
        }
    }
}
