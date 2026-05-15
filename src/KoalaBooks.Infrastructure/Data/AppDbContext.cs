using KoalaBooks.Domain.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Infrastructure.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    private readonly TenantContext _tenant;

    public AppDbContext(DbContextOptions<AppDbContext> options, TenantContext tenant) : base(options)
    {
        _tenant = tenant;
    }

    public DbSet<Organisation> Organisations => Set<Organisation>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<FiscalYear> FiscalYears => Set<FiscalYear>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalEntryLine> JournalEntryLines => Set<JournalEntryLine>();
    public DbSet<BankTransaction> BankTransactions => Set<BankTransaction>();
    public DbSet<SupplierInvoice> SupplierInvoices => Set<SupplierInvoice>();
    public DbSet<JournalEntryAttachment> JournalEntryAttachments => Set<JournalEntryAttachment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.UseOpenIddict();

        modelBuilder.Entity<Organisation>(entity =>
        {
            entity.Property(o => o.Name).HasMaxLength(200);
            entity.Property(o => o.Slug).HasMaxLength(100);
            entity.HasIndex(o => o.Slug).IsUnique();
        });

        modelBuilder.Entity<FiscalYear>()
            .HasQueryFilter(f => _tenant.OrganisationId != null && f.OrganisationId == _tenant.OrganisationId);

        modelBuilder.Entity<BankTransaction>()
            .HasQueryFilter(b => _tenant.OrganisationId != null && b.OrganisationId == _tenant.OrganisationId);

        modelBuilder.Entity<JournalEntry>()
            .HasQueryFilter(j => _tenant.OrganisationId != null && j.FiscalYear.OrganisationId == _tenant.OrganisationId);

        modelBuilder.Entity<JournalEntryLine>()
            .HasQueryFilter(l => _tenant.OrganisationId != null && l.JournalEntry.FiscalYear.OrganisationId == _tenant.OrganisationId);

        modelBuilder.Entity<SupplierInvoice>()
            .HasQueryFilter(s => _tenant.OrganisationId != null && s.FiscalYear.OrganisationId == _tenant.OrganisationId);

        modelBuilder.Entity<JournalEntryAttachment>()
            .HasQueryFilter(a => _tenant.OrganisationId != null && a.JournalEntry.FiscalYear.OrganisationId == _tenant.OrganisationId);

        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasQueryFilter(a => _tenant.OrganisationId != null && a.FiscalYear.OrganisationId == _tenant.OrganisationId);
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

        modelBuilder.Entity<JournalEntryAttachment>(entity =>
        {
            entity.Property(a => a.FileName).HasMaxLength(260);
            entity.Property(a => a.ContentType).HasMaxLength(100);
            entity.HasOne(a => a.JournalEntry)
                  .WithMany()
                  .HasForeignKey(a => a.JournalEntryId)
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
