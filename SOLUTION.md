# Solution Steps

1. Locate the current performance bottleneck in `RevenueReportService.GetMonthlyRevenueAsync` (it executes N+1 queries: customers → invoices → lines/payments/credits).

2. Align the month-close business rules with the seeded ledger snapshots:
- Invoice inclusion must use the customer billing timezone offset when determining whether an invoice belongs to the requested billing month.
- Paid/credited amounts must include late activity by totaling payments/credits associated with the selected invoices, not by payment/credit dates within the month.

3. Use `LedgerSnapshots` as the source of truth for `TotalInvoicedAmount`, `TotalPaidAmount`, and `TotalCreditedAmount` per customer for the requested `PeriodMonth` (this removes the expensive per-invoice line/payment/credit aggregation).

4. Compute the remaining per-customer fields not present on `LedgerSnapshots`:
- `InvoiceCount`: number of invoices for the customer whose *billing-adjusted* `IssuedAtUtc` falls within the requested month.
- `LatestInvoiceStatus`: the status from the latest invoice (by `IssuedAtUtc DESC, Id DESC`) within that same month-filtered invoice set.

5. Implement invoice aggregation in a single SQL query using a window function (`ROW_NUMBER`) to avoid client-side sorting/grouping. Keep the invoice date predicate index-friendly by comparing `Invoices.IssuedAtUtc` to shifted bounds instead of applying `DATEADD` to the column.

6. Preserve the response contract and ordering:
- Order customers by `CustomerCode`.
- Keep the same JSON shape by only changing the service implementation (do not alter DTOs/Controller).

7. Make logs PII-safe and reduce noise:
- Remove per-customer logging that includes `ContactEmail`.
- Replace it with one or two aggregate log lines (month, customer count, totals).

8. Ensure the endpoint returns correct results for edge cases:
- Customers with missing payments/credits rows should still return zero amounts from `LedgerSnapshots`.
- Month boundaries should match the ledger snapshot logic via billing offset handling.

