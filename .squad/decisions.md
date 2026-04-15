# Squad Decisions

## Active Decisions

### 2026-04-15: Feature Priority Roadmap (Danny + Reuben)
**By:** Danny (Lead), reviewed by Reuben (Accountant)
**Status:** Proposed — awaiting user approval

**P0 — Fix accounting-breaking bugs first:**
1. AccountClass enum mapping wrong (class 2 → all Liability, class 8 → Equity; should split equity/liability and treat class 8 as P&L)
2. Balance formulas use asset-normal math for all accounts (credit-normal accounts show inverted balances)
3. Reports include draft entries (should only aggregate posted transactions)
4. SieExportService not registered in DI (export page crashes)
5. Reversals bypass closed-year check
6. eval() in SIE download (XSS risk)
7. No date/account validation on journal lines
8. Verification number (verifikationsnummer) not implemented (required by BFL 5 kap. 6§)

**P1 — Complete the bookkeeping lifecycle:**
1. Proper year-end closing (bokslut) with closing entries to equity
2. Opening balance automation
3. Delete draft journal entries
4. Reopen fiscal year
5. Input validation hardening
6. Accounting periods within fiscal year

**P2 — Daily bookkeeping usability:**
1. VAT/moms handling (25/12/6%)
2. Search & filtering
3. Journal entry templates
4. PDF report export
5. Per-account ledger view (kontoutdrag)
6. SIE-4 export compliance verification
7. Verification series (verifikationsserier)

**Backlog:** Auth, multi-company, audit trail, Excel export, backup/restore

**Decision:** Fix P0 accounting math before building any new features. Reuben's review elevated AccountClass mapping and credit-normal balance formulas as the two most critical issues.

See full details: `.squad/decisions/inbox/danny-feature-priority-roadmap.md` and `.squad/decisions/inbox/reuben-roadmap-accounting-review.md`

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
