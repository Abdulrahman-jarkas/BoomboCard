using System.Globalization;
using BoomboCard.SharedKernel;
using CsvHelper;
using CsvHelper.Configuration;

namespace BoomboCard.Catalog.Application.Exports;

/// <summary>One flattened row of the export. One column per country price.</summary>
public sealed record ProductExportRow
{
    public string Sku { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public int StockQuantity { get; init; }

    public int LowStockThreshold { get; init; }

    public decimal? CostPrice { get; init; }

    public string? CostPriceCurrency { get; init; }

    /// <summary>Keyed by country code. Rendered as one column per configured country.</summary>
    public Dictionary<string, decimal?> PricesByCountry { get; init; } = [];
}

/// <summary>
/// Writes the export CSV.
///
/// Onyx built its CSV by string concatenation:
/// <c>result += $"{p.Sku},{p.Name},{p.Price},{p.StockQuantity}\n";</c>
///
/// Three things wrong with that, all fixed here:
///
///  - <b>A33, no quoting.</b> Any product name containing a comma shifted every column after
///    it — and product names came from import, so they were free text. CsvHelper quotes and
///    escapes properly.
///  - <b>A33, formula injection.</b> A product named <c>=cmd|…</c> executes when the file opens
///    in Excel. Neutralised below.
///  - <b>Culture.</b> It used the ambient culture, so a decimal comma would have produced
///    <c>89,99</c> in a comma-separated file. InvariantCulture, always, for machine-readable
///    formats.
/// </summary>
internal sealed class ProductCsvWriter
{
    private static readonly char[] FormulaTriggers = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>
    /// Column order is fixed and includes every configured country, so a round trip through
    /// Excel and back into the importer has a stable, predictable shape.
    /// </summary>
    public static async Task<int> WriteAsync(
        Stream destination,
        IAsyncEnumerable<ProductExportRow> rows,
        CancellationToken cancellationToken = default)
    {
        var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            // Quote whenever the value could otherwise be misread.
            ShouldQuote = _ => true
        };

        await using var textWriter = new StreamWriter(destination, leaveOpen: true);
        await using var csv = new CsvWriter(textWriter, configuration);

        var countries = Country.All.Select(country => country.Code).ToArray();

        csv.WriteField("Sku");
        csv.WriteField("Name");
        csv.WriteField("Status");
        csv.WriteField("StockQuantity");
        csv.WriteField("LowStockThreshold");
        csv.WriteField("CostPrice");
        csv.WriteField("CostPriceCurrency");

        foreach (var code in countries)
        {
            csv.WriteField($"Price_{code}");
        }

        await csv.NextRecordAsync();

        var written = 0;

        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            csv.WriteField(Neutralise(row.Sku));
            csv.WriteField(Neutralise(row.Name));
            csv.WriteField(row.Status);
            csv.WriteField(row.StockQuantity);
            csv.WriteField(row.LowStockThreshold);
            csv.WriteField(row.CostPrice?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty);
            csv.WriteField(row.CostPriceCurrency ?? string.Empty);

            foreach (var code in countries)
            {
                var price = row.PricesByCountry.GetValueOrDefault(code);
                csv.WriteField(price?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty);
            }

            await csv.NextRecordAsync();
            written++;
        }

        await csv.FlushAsync();
        await textWriter.FlushAsync(cancellationToken);

        return written;
    }

    /// <summary>
    /// Prefixes a leading formula character with an apostrophe, so Excel treats the cell as
    /// text. Without this, an attacker who can name a product can run a formula on the machine
    /// of whoever opens the export.
    /// </summary>
    private static string Neutralise(string value) =>
        value.Length > 0 && FormulaTriggers.Contains(value[0]) ? "'" + value : value;
}
