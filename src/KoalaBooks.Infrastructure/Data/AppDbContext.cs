using KoalaBooks.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<FiscalYear> FiscalYears => Set<FiscalYear>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalEntryLine> JournalEntryLines => Set<JournalEntryLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasIndex(a => new { a.FiscalYearId, a.AccountNumber }).IsUnique();
            entity.Property(a => a.AccountNumber).HasMaxLength(10);
            entity.Property(a => a.Name).HasMaxLength(200);
            entity.HasOne(a => a.FiscalYear)
                  .WithMany(f => f.Accounts)
                  .HasForeignKey(a => a.FiscalYearId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FiscalYear>(entity =>
        {
            entity.Property(f => f.Name).HasMaxLength(100);
        });

        modelBuilder.Entity<JournalEntry>(entity =>
        {
            entity.HasIndex(j => new { j.FiscalYearId, j.EntryNumber }).IsUnique();
            entity.Property(j => j.Description).HasMaxLength(500);
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
    }
}
