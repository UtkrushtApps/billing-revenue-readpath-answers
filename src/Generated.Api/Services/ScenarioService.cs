using System.Globalization;
using Generated.Api.Data;
using Generated.Api.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;

namespace Generated.Api.Services;

public interface IRevenueReportService
{
    Task<RevenueReportResponse> GetMonthlyRevenueAsync(string month, CancellationToken cancellationToken);
}

public sealed class RevenueReportService(AppDbContext dbContext, ILogger<RevenueReportService> logger) : IRevenueReportService
{
    private sealed class SnapshotCustomer
    {
        public int CustomerId { get; init; }
        public string CustomerCode { get; init; } = string.Empty;
        public string CustomerName { get; init; } = string.Empty;
        public string BillingTimeZone { get; init; } = string.Empty;
        public decimal InvoicedAmount { get; init; }
        public decimal PaidAmount { get; init; }
        public decimal CreditedAmount { get; init; }
    }

    private sealed class InvoiceAggregate
    {
        public int CustomerId { get; init; }
        public int InvoiceCount { get; init; }
        public string? LatestInvoiceStatus { get; init; }
    }

    public async Task<RevenueReportResponse> GetMonthlyRevenueAsync(string month, CancellationToken cancellationToken)
    {
        if (!DateTime.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedMonth))
        {
            throw new ArgumentException("Month must be in yyyy-MM format.", nameof(month));
        }

        // The seeded LedgerSnapshots are stored with PeriodMonth = first day of the (billing) month (date-only).
        var monthStartLocal = parsedMonth.Date;
        var monthEndLocal = monthStartLocal.AddMonths(1);

        logger.LogInformation("Generating monthly revenue report for {Month}", month);

        // 1) Read totals directly from LedgerSnapshots (already aligned to month-close rules, including late payments/credits).
        var snapshotCustomers = await (
            from ls in dbContext.LedgerSnapshots.AsNoTracking()
            join c in dbContext.Customers.AsNoTracking() on ls.CustomerId equals c.Id
            where ls.PeriodMonth == monthStartLocal
            orderby c.CustomerCode
            select new SnapshotCustomer
            {
                CustomerId = c.Id,
                CustomerCode = c.CustomerCode,
                CustomerName = c.DisplayName,
                BillingTimeZone = c.BillingTimeZone,
                InvoicedAmount = ls.InvoicedAmount,
                PaidAmount = ls.PaidAmount,
                CreditedAmount = ls.CreditedAmount
            })
            .ToListAsync(cancellationToken);

        if (snapshotCustomers.Count == 0)
        {
            return new RevenueReportResponse(
                month,
                DateTime.UtcNow,
                0,
                0m,
                0m,
                0m,
                Array.Empty<CustomerRevenueDto>());
        }

        // 2) Compute month-specific invoice count + latest invoice status.
        //    Must match LedgerSnapshots month boundary logic:
        //    DATEADD(minute, BillingUtcOffsetMinutes, IssuedAtUtc) is within [monthStartLocal, monthEndLocal).
        //    To keep invoice filtering index-friendly, we compare IssuedAtUtc to shifted bounds.
        var periodMonthParam = new SqlParameter("@periodMonth", monthStartLocal);
        var monthStartParam = new SqlParameter("@monthStartLocal", monthStartLocal);
        var monthEndParam = new SqlParameter("@monthEndLocal", monthEndLocal);

        var invoiceAggregates = await dbContext.Database
            .SqlQuery<InvoiceAggregate>(
                @"WITH MonthInvoices AS
                  (
                      SELECT
                          i.CustomerId,
                          i.Status,
                          i.IssuedAtUtc,
                          i.Id,
                          ROW_NUMBER() OVER (
                              PARTITION BY i.CustomerId
                              ORDER BY i.IssuedAtUtc DESC, i.Id DESC
                          ) AS rn
                      FROM dbo.LedgerSnapshots ls
                      INNER JOIN dbo.Invoices i ON i.CustomerId = ls.CustomerId
                      INNER JOIN dbo.Customers c ON c.Id = i.CustomerId
                      WHERE ls.PeriodMonth = @periodMonth
                        AND i.IssuedAtUtc >= DATEADD(minute, -c.BillingUtcOffsetMinutes, @monthStartLocal)
                        AND i.IssuedAtUtc <  DATEADD(minute, -c.BillingUtcOffsetMinutes, @monthEndLocal)
                  )
                  SELECT
                      CustomerId,
                      COUNT(*) AS InvoiceCount,
                      MAX(CASE WHEN rn = 1 THEN Status END) AS LatestInvoiceStatus
                  FROM MonthInvoices
                  GROUP BY CustomerId;",
                periodMonthParam,
                monthStartParam,
                monthEndParam)
            .ToListAsync(cancellationToken);

        var invoiceAggByCustomerId = invoiceAggregates.ToDictionary(x => x.CustomerId);

        // 3) Project final response shape, preserving ordering by CustomerCode.
        var rows = new List<CustomerRevenueDto>(snapshotCustomers.Count);
        foreach (var sc in snapshotCustomers)
        {
            invoiceAggByCustomerId.TryGetValue(sc.CustomerId, out var invAgg);
            var invoiceCount = invAgg?.InvoiceCount ?? 0;
            var latestStatus = invAgg?.LatestInvoiceStatus;

            rows.Add(new CustomerRevenueDto(
                sc.CustomerCode,
                sc.CustomerName,
                sc.BillingTimeZone,
                sc.InvoicedAmount,
                sc.PaidAmount,
                sc.CreditedAmount,
                sc.InvoicedAmount - sc.CreditedAmount,
                invoiceCount,
                latestStatus));
        }

        var totalInvoiced = rows.Sum(r => r.InvoicedAmount);
        var totalPaid = rows.Sum(r => r.PaidAmount);
        var totalCredited = rows.Sum(r => r.CreditedAmount);

        logger.LogInformation(
            "Revenue report for {Month} ready. Customers={CustomerCount} Totals(Invoiced={Invoiced}, Paid={Paid}, Credited={Credited})",
            month,
            rows.Count,
            totalInvoiced,
            totalPaid,
            totalCredited);

        return new RevenueReportResponse(
            month,
            DateTime.UtcNow,
            rows.Count,
            totalInvoiced,
            totalPaid,
            totalCredited,
            rows);
    }
}
