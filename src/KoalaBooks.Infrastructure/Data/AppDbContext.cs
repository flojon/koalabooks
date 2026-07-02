using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Infrastructure.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    private readonly ICurrentUser _currentUser;

    public AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser currentUser) : base(options)
    {
        _currentUser = currentUser;
    }

    public DbSet<Organisation> Organisations => Set<Organisation>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<FiscalYear> FiscalYears => Set<FiscalYear>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalEntryLine> JournalEntryLines => Set<JournalEntryLine>();
    public DbSet<VoucherGapExplanation> VoucherGapExplanations => Set<VoucherGapExplanation>();
    public DbSet<BankTransaction> BankTransactions => Set<BankTransaction>();
    public DbSet<SupplierInvoice> SupplierInvoices => Set<SupplierInvoice>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerInvoice> CustomerInvoices => Set<CustomerInvoice>();
    public DbSet<CustomerInvoiceLine> CustomerInvoiceLines => Set<CustomerInvoiceLine>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentData> DocumentData => Set<DocumentData>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.UseOpenIddict();

        modelBuilder.Entity<Organisation>(entity =>
        {
            entity.Property(o => o.Name).HasMaxLength(200);
            entity.Property(o => o.Slug).HasMaxLength(100);
            entity.Property(o => o.OrgNumber).HasMaxLength(20);
            entity.HasIndex(o => o.Slug).IsUnique();
        });

        modelBuilder.Entity<FiscalYear>()
            .HasQueryFilter(f => _currentUser.OrganisationId != null && f.OrganisationId == _currentUser.OrganisationId);

        modelBuilder.Entity<BankTransaction>()
            .HasQueryFilter(b => _currentUser.OrganisationId != null && b.OrganisationId == _currentUser.OrganisationId);

        modelBuilder.Entity<JournalEntry>()
            .HasQueryFilter(j => _currentUser.OrganisationId != null && j.FiscalYear.OrganisationId == _currentUser.OrganisationId);

        modelBuilder.Entity<JournalEntryLine>()
            .HasQueryFilter(l => _currentUser.OrganisationId != null && l.JournalEntry.FiscalYear.OrganisationId == _currentUser.OrganisationId);

        modelBuilder.Entity<SupplierInvoice>()
            .HasQueryFilter(s => _currentUser.OrganisationId != null && s.FiscalYear.OrganisationId == _currentUser.OrganisationId);

        modelBuilder.Entity<Customer>()
            .HasQueryFilter(c => _currentUser.OrganisationId != null && c.OrganisationId == _currentUser.OrganisationId);

        modelBuilder.Entity<CustomerInvoice>()
            .HasQueryFilter(i => _currentUser.OrganisationId != null && i.FiscalYear.OrganisationId == _currentUser.OrganisationId);

        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasQueryFilter(a => _currentUser.OrganisationId != null && a.FiscalYear.OrganisationId == _currentUser.OrganisationId);
            entity.HasIndex(a => new { a.FiscalYearId, a.AccountNumber }).IsUnique();
            entity.Property(a => a.AccountNumber).HasMaxLength(10);
            entity.Property(a => a.Name).HasMaxLength(200);
            entity.Property(a => a.IncomingBalance).HasPrecision(18, 2);
            entity.Property(a => a.OutgoingBalance).HasPrecision(18, 2);
            entity.HasOne(a => a.FiscalYear)
                  .WithMany(f => f.Accounts)
                  .HasForeignKey(a => a.FiscalYearId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FiscalYear>(entity =>
        {
            entity.Property(f => f.Name).HasMaxLength(100);
            entity.Property(f => f.ClosedAt);
            entity.HasOne(f => f.Organisation)
                  .WithMany()
                  .HasForeignKey(f => f.OrganisationId)
                  .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<FiscalYear>()
                  .WithMany()
                  .HasForeignKey(f => f.PreviousFiscalYearId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<JournalEntry>(entity =>
        {
            entity.HasIndex(j => new { j.FiscalYearId, j.EntryNumber }).IsUnique();
            entity.Property(j => j.Description).HasMaxLength(500);
            entity.Property(j => j.IsClosingEntry).HasDefaultValue(false);
            entity.HasOne(j => j.FiscalYear)
                  .WithMany(f => f.JournalEntries)
                  .HasForeignKey(j => j.FiscalYearId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<JournalEntryLine>(entity =>
        {
            entity.Property(l => l.DebitAmount).HasPrecision(18, 2);
            entity.Property(l => l.CreditAmount).HasPrecision(18, 2);
            entity.HasOne(l => l.JournalEntry)
                  .WithMany(j => j.Lines)
                  .HasForeignKey(l => l.JournalEntryId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(l => l.Account)
                  .WithMany()
                  .HasForeignKey(l => l.AccountId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<VoucherGapExplanation>(entity =>
        {
            entity.HasQueryFilter(v => _currentUser.OrganisationId != null && v.FiscalYear.OrganisationId == _currentUser.OrganisationId);
            entity.HasIndex(v => new { v.FiscalYearId, v.MissingEntryNumber }).IsUnique();
            entity.Property(v => v.Explanation).HasMaxLength(1000);
            entity.Property(v => v.ExplainedBy).HasMaxLength(200);
            entity.HasOne(v => v.FiscalYear)
                  .WithMany()
                  .HasForeignKey(v => v.FiscalYearId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SupplierInvoice>(entity =>
        {
            entity.Property(s => s.SupplierName).HasMaxLength(200);
            entity.Property(s => s.InvoiceNumber).HasMaxLength(100);
            entity.Property(s => s.Notes).HasMaxLength(500);
            entity.Property(s => s.AmountExclVat).HasPrecision(18, 2);
            entity.Property(s => s.VatAmount).HasPrecision(18, 2);
            entity.Property(s => s.TotalAmount).HasPrecision(18, 2);
            entity.HasOne(s => s.FiscalYear)
                  .WithMany()
                  .HasForeignKey(s => s.FiscalYearId)
                  .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(s => s.JournalEntry)
                  .WithMany()
                  .HasForeignKey(s => s.JournalEntryId)
                  .OnDelete(DeleteBehavior.SetNull)
                  .IsRequired(false);
            entity.HasOne(s => s.PaymentJournalEntry)
                  .WithMany()
                  .HasForeignKey(s => s.PaymentJournalEntryId)
                  .OnDelete(DeleteBehavior.SetNull)
                  .IsRequired(false);
        });

        modelBuilder.Entity<Customer>(entity =>
        {
            entity.Property(c => c.Name).HasMaxLength(200);
            entity.Property(c => c.OrgNumber).HasMaxLength(20);
            entity.Property(c => c.Email).HasMaxLength(200);
            entity.Property(c => c.Phone).HasMaxLength(50);
            entity.Property(c => c.Address).HasMaxLength(300);
            entity.Property(c => c.PostalCode).HasMaxLength(20);
            entity.Property(c => c.City).HasMaxLength(100);
            entity.Property(c => c.Country).HasMaxLength(2).HasDefaultValue("SE");
            entity.HasOne(c => c.Organisation)
                  .WithMany()
                  .HasForeignKey(c => c.OrganisationId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CustomerInvoice>(entity =>
        {
            entity.HasIndex(i => new { i.FiscalYearId, i.InvoiceNumber }).IsUnique();
            entity.Property(i => i.CustomerName).HasMaxLength(200);
            entity.Property(i => i.CustomerOrgNumber).HasMaxLength(20);
            entity.Property(i => i.CustomerAddress).HasMaxLength(300);
            entity.Property(i => i.CustomerPostalCode).HasMaxLength(20);
            entity.Property(i => i.CustomerCity).HasMaxLength(100);
            entity.Property(i => i.OurReference).HasMaxLength(200);
            entity.Property(i => i.YourReference).HasMaxLength(200);
            entity.Property(i => i.Notes).HasMaxLength(500);
            entity.Property(i => i.AmountExclVat).HasPrecision(18, 2);
            entity.Property(i => i.VatAmount).HasPrecision(18, 2);
            entity.Property(i => i.TotalAmount).HasPrecision(18, 2);
            entity.HasOne(i => i.FiscalYear)
                  .WithMany()
                  .HasForeignKey(i => i.FiscalYearId)
                  .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(i => i.Customer)
                  .WithMany()
                  .HasForeignKey(i => i.CustomerId)
                  .OnDelete(DeleteBehavior.SetNull)
                  .IsRequired(false);
            entity.HasOne(i => i.JournalEntry)
                  .WithMany()
                  .HasForeignKey(i => i.JournalEntryId)
                  .OnDelete(DeleteBehavior.SetNull)
                  .IsRequired(false);
            entity.HasOne(i => i.PaymentJournalEntry)
                  .WithMany()
                  .HasForeignKey(i => i.PaymentJournalEntryId)
                  .OnDelete(DeleteBehavior.SetNull)
                  .IsRequired(false);
        });

        modelBuilder.Entity<CustomerInvoiceLine>(entity =>
        {
            entity.Property(l => l.Description).HasMaxLength(500);
            entity.Property(l => l.Quantity).HasPrecision(18, 4);
            entity.Property(l => l.UnitPrice).HasPrecision(18, 2);
            entity.Property(l => l.AmountExclVat).HasPrecision(18, 2);
            entity.Property(l => l.VatAmount).HasPrecision(18, 2);
            entity.Property(l => l.TotalAmount).HasPrecision(18, 2);
            entity.HasOne(l => l.CustomerInvoice)
                  .WithMany(i => i.Lines)
                  .HasForeignKey(l => l.CustomerInvoiceId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Document>(entity =>
        {
            entity.Property(d => d.FileName).HasMaxLength(260);
            entity.Property(d => d.ContentType).HasMaxLength(100);
            entity.Property(d => d.StorageKey).HasMaxLength(500);
            entity.Property(d => d.SuggestedType).HasMaxLength(50);
            entity.Property(d => d.ClassifiedType).HasMaxLength(50);
            entity.HasQueryFilter(d => _currentUser.OrganisationId != null && d.OrganisationId == _currentUser.OrganisationId);

            entity.HasOne<Organisation>()
                  .WithMany()
                  .HasForeignKey(d => d.OrganisationId)
                  .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(d => d.OrganisationId);

            entity.HasMany(d => d.JournalEntries)
                  .WithMany(j => j.Documents)
                  .UsingEntity("DocumentJournalEntries");

            entity.HasMany(d => d.SupplierInvoices)
                  .WithMany(s => s.Documents)
                  .UsingEntity("DocumentSupplierInvoices");

            entity.HasMany(d => d.CustomerInvoices)
                  .WithMany(c => c.Documents)
                  .UsingEntity("DocumentCustomerInvoices");
        });

        modelBuilder.Entity<DocumentData>(entity =>
        {
            entity.HasKey(d => d.DocumentId);
            entity.HasOne(d => d.Document)
                  .WithOne()
                  .HasForeignKey<DocumentData>(d => d.DocumentId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BankTransaction>(entity =>
        {
            entity.Property(b => b.Amount).HasPrecision(18, 2);
            entity.Property(b => b.Description).HasMaxLength(500);
            entity.Property(b => b.Reference).HasMaxLength(200);
            entity.HasOne(b => b.Organisation)
                  .WithMany()
                  .HasForeignKey(b => b.OrganisationId)
                  .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(b => b.Account)
                  .WithMany()
                  .HasForeignKey(b => b.AccountId)
                  .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(b => b.JournalEntry)
                  .WithMany()
                  .HasForeignKey(b => b.JournalEntryId)
                  .OnDelete(DeleteBehavior.SetNull)
                  .IsRequired(false);
        });
    }
}
