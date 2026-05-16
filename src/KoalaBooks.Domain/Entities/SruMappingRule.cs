using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Domain.Entities;

public class SruMappingRule
{
    public int Id { get; set; }
    public LegalForm LegalForm { get; set; }
    public int SruCode { get; set; }
    public string? RadLabel { get; set; }
    public string Description { get; set; } = "";

    // Comma-separated account patterns: exact numbers ("1088"), ranges ("1000-1087"),
    // wildcards ("112x", "17xx"), or wildcard ranges ("151x-155x").
    public string AccountPatterns { get; set; } = "";

    public SruSignFilter Sign { get; set; } = SruSignFilter.Any;
}
