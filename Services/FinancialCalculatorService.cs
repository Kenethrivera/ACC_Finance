using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using acc_finance.Models; // Make sure this matches your namespace for GivingRecord, etc.
using static Supabase.Postgrest.Constants;

namespace acc_finance.Services
{
    public class FinancialCalculatorService
    {
        private readonly SupabaseService _supabase;

        public FinancialCalculatorService(SupabaseService supabase)
        {
            _supabase = supabase;
        }

        public async Task<decimal> CalculateIncomeForFundAsync(DateTime start, DateTime end, string fund)
        {
            var givingResp = await _supabase.Client.From<GivingRecord>()
                .Filter("service_date", Operator.GreaterThanOrEqual, start.ToString("yyyy-MM-dd"))
                .Filter("service_date", Operator.LessThanOrEqual, end.ToString("yyyy-MM-dd")).Get();
            var records = givingResp.Models ?? new List<GivingRecord>();
            var recordIds = records.Select(r => (object)r.Id).ToList();

            var allEntries = new List<GivingEntry>();
            if (recordIds.Any())
            {
                var entryResp = await _supabase.Client.From<GivingEntry>().Filter("giving_record_id", Operator.In, recordIds).Get();
                allEntries = entryResp.Models ?? new List<GivingEntry>();
            }

            decimal total = 0;
            if (fund.Equals("General", StringComparison.OrdinalIgnoreCase))
            {
                total += allEntries.Sum(e => e.Tithes + e.Offerings) + allEntries.Where(e => string.IsNullOrWhiteSpace(e.OthersFund) || e.OthersFund.Trim().Equals("General", StringComparison.OrdinalIgnoreCase)).Sum(e => e.Others);
            }
            else if (fund.Equals("Pledges", StringComparison.OrdinalIgnoreCase))
            {
                total += allEntries.Sum(e => e.Solomon + e.Noah + e.Mission) + allEntries.Where(e => !string.IsNullOrWhiteSpace(e.OthersFund) && e.OthersFund.Trim().Equals("Pledges", StringComparison.OrdinalIgnoreCase)).Sum(e => e.Others);
            }
            else
            {
                total += allEntries.Where(e => !string.IsNullOrWhiteSpace(e.OthersFund) && e.OthersFund.Trim().Equals(fund, StringComparison.OrdinalIgnoreCase)).Sum(e => e.Others);
            }

            return total;
        }

        public async Task<decimal> CalculateExpenseForFundAsync(DateTime start, DateTime end, string fund)
        {
            var disbResp = await _supabase.Client.From<DisbursementRecord>()
                .Filter("record_date", Operator.GreaterThanOrEqual, start.ToString("yyyy-MM-dd"))
                .Filter("record_date", Operator.LessThanOrEqual, end.ToString("yyyy-MM-dd")).Get();
            var records = disbResp.Models ?? new List<DisbursementRecord>();
            var recordIds = records.Select(r => (object)r.Id).ToList();

            var allItems = new List<VoucherItem>();
            if (recordIds.Any())
            {
                var vResp = await _supabase.Client.From<Voucher>().Filter("disbursement_record_id", Operator.In, recordIds).Get();
                var vouchers = vResp.Models ?? new List<Voucher>();
                var voucherIds = vouchers.Select(v => (object)v.Id).ToList();

                if (voucherIds.Any())
                {
                    var iResp = await _supabase.Client.From<VoucherItem>().Filter("voucher_id", Operator.In, voucherIds).Get();
                    allItems = iResp.Models ?? new List<VoucherItem>();
                }
            }

            decimal total = 0;
            foreach (var item in allItems)
            {
                decimal netExp = item.Amount - item.AmountReturned;
                if (netExp <= 0) continue;
                string fundSource = string.IsNullOrWhiteSpace(item.FundSource) ? "General" : item.FundSource.Trim();
                if (fundSource.Equals(fund, StringComparison.OrdinalIgnoreCase))
                {
                    total += netExp;
                }
            }
            return total;
        }


    }
}