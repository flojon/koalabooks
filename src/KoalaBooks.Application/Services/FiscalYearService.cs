using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

public class FiscalYearService
{
    private readonly AppDbContext _db;

    public FiscalYearService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<FiscalYear>> GetAllAsync()
    {
        return await _db.FiscalYears
            .OrderByDescending(f => f.StartDate)
            .ToListAsync();
    }

    public async Task<FiscalYear?> GetByIdAsync(int id)
    {
        return await _db.FiscalYears.FindAsync(id);
    }

    public async Task<FiscalYear?> GetActiveAsync()
    {
        return await _db.FiscalYears
            .Where(f => !f.IsClosed)
            .OrderByDescending(f => f.StartDate)
            .FirstOrDefaultAsync();
    }

    public async Task<FiscalYear> CreateAsync(FiscalYear fiscalYear)
    {
        _db.FiscalYears.Add(fiscalYear);
        await _db.SaveChangesAsync();
        return fiscalYear;
    }

    public async Task CloseAsync(int id)
    {
        var fy = await _db.FiscalYears.FindAsync(id);
        if (fy is not null)
        {
            fy.IsClosed = true;
            await _db.SaveChangesAsync();
        }
    }
}
