using System.Globalization;
using System.Text;

namespace KoalaBooks.Application.Services;

public static class VatReportCsvExporter
{
    private static readonly CultureInfo SvSe = CultureInfo.GetCultureInfo("sv-SE");
    private const string Separator = ";";

    public static byte[] Build(VatReportData data, string fiscalYearName, DateOnly? from, DateOnly? to)
    {
        var sb = new StringBuilder();

        sb.Append("Momsredovisning").Append(Separator).AppendLine(Escape(fiscalYearName));
        if (from.HasValue || to.HasValue)
        {
            sb.Append("Period").Append(Separator)
              .Append(from?.ToString("yyyy-MM-dd") ?? "")
              .Append(" — ")
              .AppendLine(to?.ToString("yyyy-MM-dd") ?? "");
        }
        sb.AppendLine();

        AppendSection(sb, data.OutputVat, isInput: false);
        sb.AppendLine();
        AppendSection(sb, data.InputVat, isInput: true);
        sb.AppendLine();

        sb.Append(data.NetPayable >= 0 ? "Moms att betala" : "Moms att återfå")
          .Append(Separator)
          .AppendLine(Math.Abs(data.NetPayable).ToString("0.00", SvSe));

        var csv = sb.ToString();
        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(csv);
        var output = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, output, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, output, preamble.Length, body.Length);
        return output;
    }

    private static void AppendSection(StringBuilder sb, VatReportSection section, bool isInput)
    {
        sb.AppendLine(Escape(section.Title));
        sb.Append("Konto").Append(Separator)
          .Append("Namn").Append(Separator)
          .Append("Debet").Append(Separator)
          .Append("Kredit").Append(Separator)
          .AppendLine("Netto");

        foreach (var row in section.Rows)
        {
            var net = isInput ? row.Debit - row.Credit : row.Credit - row.Debit;
            sb.Append(Escape(row.AccountNumber)).Append(Separator)
              .Append(Escape(row.AccountName)).Append(Separator)
              .Append(row.Debit.ToString("0.00", SvSe)).Append(Separator)
              .Append(row.Credit.ToString("0.00", SvSe)).Append(Separator)
              .AppendLine(net.ToString("0.00", SvSe));
        }

        sb.Append("Summa").Append(Separator).Append(Separator).Append(Separator).Append(Separator)
          .AppendLine(section.Total.ToString("0.00", SvSe));
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(Separator) || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
