namespace KoalaBooks.Domain.Enums;

public enum DocumentEntityType { JournalEntry, SupplierInvoice, CustomerInvoice }

public enum LinkOutcome { Linked, DocumentNotFound, EntityNotFound, ConcurrencyConflict }
