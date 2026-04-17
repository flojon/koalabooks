# Income Statement IB Consistency Fix

**By:** Linus (Backend Dev)
**Date:** 2026-04-18
**Status:** Implemented

## Decision

The income statement now includes `IncomingBalance` for P&L accounts when viewing the full fiscal year (no date filters). This makes it consistent with `YearEndClosingService`, which computes P&L balances as `IB + transactions`.

## Context

- `GetIncomeStatementAsync` previously only summed transaction debits/credits, ignoring `IncomingBalance`.
- `YearEndClosingService.GetPnLAccountBalancesAsync` and `ExecuteClosingAsync` compute balances as `IB + Credit - Debit` (revenue) or `IB + Debit - Credit` (expense).
- When P&L accounts have non-zero IB (e.g., from SIE import in the first fiscal year), the income statement showed different totals than what the closing service closed.
- After year-end closing, `PropagateBalancesToNextYearAsync` sets P&L IB to 0 for the next year, so this fix has zero impact on subsequent years.

## Rules

- **Full-year income statement** (no date filter): Amount = IB + net transactions. Matches closing service computation.
- **Sub-period income statement** (date filter): Amount = net transactions only. IB is excluded because it represents the year's starting point, not the sub-period's activity.
- Accounts with only IB (no transactions) now appear in the full-year report.
