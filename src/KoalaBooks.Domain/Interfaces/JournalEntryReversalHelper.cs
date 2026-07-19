namespace KoalaBooks.Domain.Interfaces;

public static class JournalEntryReversalHelper
{
    // Single source of truth for the reversal entry's description text, so the live
    // dialog preview, the persisted entry, and tests all stay in sync on wording/format.
    public static string BuildReversalDescription(int originalEntryNumber, string reason) =>
        $"Återföring av #{originalEntryNumber}: {reason}";
}
