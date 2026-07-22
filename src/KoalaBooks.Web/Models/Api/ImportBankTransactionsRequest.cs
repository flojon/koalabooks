using System.ComponentModel.DataAnnotations;

namespace KoalaBooks.Web.Models.Api;

public class BankTransactionImportRow
{
    public int RowIndex { get; init; }
    public DateOnly? Date { get; init; }
    public decimal? Amount { get; init; }
    public string Description { get; init; } = "";
    public string? Reference { get; init; }
    public bool IsDuplicate { get; init; }
    public string? ParseError { get; init; }
}

public class ImportBankTransactionsRequest
{
    [Required]
    [MinLength(1)]
    public List<BankTransactionImportRow> Transactions { get; init; } = [];
}
