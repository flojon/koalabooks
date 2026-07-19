namespace KoalaBooks.Application.Services;

// Scoped (per Blazor Server circuit): remembers the last fiscal year a user explicitly
// picked on any page in the transactional/reporting page cluster, so navigating between
// them (e.g. BankImport -> GeneralLedger) doesn't reset to "today's year" every time.
// Deliberately NOT a global source of truth - pages seed their default from this, but the
// user can always override it locally, and organisation-wide pages (Todo/Review/Inbox)
// never read from it.
public sealed class FiscalYearSelectionContext
{
    public int? LastSelectedFiscalYearId { get; private set; }

    public void Set(int fiscalYearId) => LastSelectedFiscalYearId = fiscalYearId;
}
