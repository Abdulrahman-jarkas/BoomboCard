using System.Globalization;
using BoomboCard.Catalog.Domain.ProductAggregate;
using BoomboCard.SharedKernel;
using CsvHelper;
using CsvHelper.Configuration;

namespace BoomboCard.Catalog.Application.Imports;

/// <summary>One validated row, ready to apply. Every value here is already known to be good.</summary>
internal sealed record ParsedProductRow(
    int LineNumber,
    Sku Sku,
    string Name,
    ProductStatusValue? Status,
    int? StockQuantity,
    int? LowStockThreshold,
    Money? CostPrice,
    IReadOnlyDictionary<string, Money> PricesByCountry);

internal sealed record RowError(int LineNumber, string? Sku, string Message);

internal sealed record ParseOutcome(List<ParsedProductRow> Rows, List<RowError> Errors);

/// <summary>
/// Parses and validates a product CSV.
///
/// Onyx's importer (A32–A37) got every one of these wrong:
///
///  - <b>A34.</b> It split the whole file on <c>'\n'</c>, so a file saved on Windows left a
///    stray <c>'\r'</c> on the last field of every row. CsvHelper handles CRLF, LF, quoted
///    fields containing delimiters or newlines, and a UTF-8 BOM.
///  - <b>A35.</b> It used <c>decimal.Parse</c> and <c>int.Parse</c> with no <c>TryParse</c> and
///    the ambient culture, so one bad cell threw — and because it called
///    <c>SaveChangesAsync</c> inside the row loop, every row before the bad one was already
///    committed. Here nothing is written until the whole file has passed, and every number is
///    parsed with InvariantCulture.
///  - <b>Duplicate SKUs inside one file</b> were silently inserted twice. Detected here.
///
/// The format is the mirror image of the export, so export → edit in Excel → re-import is a
/// real round trip. Onyx advertised that loop and it worked in neither direction.
/// </summary>
internal static class ProductCsvReader
{
    public const int MaxRows = 10_000;

    private const string SkuColumn = "Sku";
    private const string NameColumn = "Name";
    private const string StatusColumn = "Status";
    private const string StockColumn = "StockQuantity";
    private const string ThresholdColumn = "LowStockThreshold";
    private const string CostPriceColumn = "CostPrice";
    private const string CostCurrencyColumn = "CostPriceCurrency";

    public static async Task<ParseOutcome> ReadAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<ParsedProductRow>();
        var errors = new List<RowError>();

        var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            // A spreadsheet produced by a human will have surprises. Tolerate them and report
            // them per row rather than throwing on the whole file.
            TrimOptions = TrimOptions.Trim,
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null
        };

        // detectEncodingFromByteOrderMarks strips a UTF-8 BOM, which Excel writes by default and
        // which would otherwise corrupt the first header name.
        using var reader = new StreamReader(source, detectEncodingFromByteOrderMarks: true);
        using var csv = new CsvReader(reader, configuration);

        if (!await csv.ReadAsync())
        {
            errors.Add(new RowError(0, null, "The file is empty."));
            return new ParseOutcome(rows, errors);
        }

        csv.ReadHeader();

        var header = csv.HeaderRecord ?? [];

        foreach (var required in new[] { SkuColumn, NameColumn })
        {
            if (!header.Contains(required, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(new RowError(1, null, $"Required column '{required}' is missing."));
            }
        }

        if (errors.Count > 0)
        {
            return new ParseOutcome(rows, errors);
        }

        var seenSkus = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lineNumber = 1;

        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;

            if (rows.Count + errors.Count >= MaxRows)
            {
                errors.Add(new RowError(
                    lineNumber,
                    null,
                    $"The file exceeds the {MaxRows:N0} row limit. Split it, or use the bulk pipeline."));
                break;
            }

            var parsed = ParseRow(csv, lineNumber, seenSkus);

            if (parsed.Error is not null)
            {
                errors.Add(parsed.Error);
            }
            else if (parsed.Row is not null)
            {
                seenSkus[parsed.Row.Sku.Value] = lineNumber;
                rows.Add(parsed.Row);
            }
        }

        if (rows.Count == 0 && errors.Count == 0)
        {
            errors.Add(new RowError(1, null, "The file contains a header but no data rows."));
        }

        return new ParseOutcome(rows, errors);
    }

    private static (ParsedProductRow? Row, RowError? Error) ParseRow(
        CsvReader csv,
        int lineNumber,
        Dictionary<string, int> seenSkus)
    {
        var rawSku = Field(csv, SkuColumn);

        var sku = Sku.Create(rawSku);

        if (sku.IsError)
        {
            return (null, new RowError(lineNumber, rawSku, sku.FirstError.Description));
        }

        if (seenSkus.TryGetValue(sku.Value.Value, out var firstLine))
        {
            return (null, new RowError(
                lineNumber,
                sku.Value.Value,
                $"Duplicate SKU — already present on line {firstLine}."));
        }

        var name = Field(csv, NameColumn);

        if (string.IsNullOrWhiteSpace(name))
        {
            return (null, new RowError(lineNumber, sku.Value.Value, "Name is required."));
        }

        if (name.Trim().Length > Product.MaxNameLength)
        {
            return (null, new RowError(
                lineNumber,
                sku.Value.Value,
                $"Name exceeds {Product.MaxNameLength} characters."));
        }

        ProductStatusValue? status = null;
        var rawStatus = Field(csv, StatusColumn);

        if (!string.IsNullOrWhiteSpace(rawStatus))
        {
            if (!Enum.TryParse<ProductStatusValue>(rawStatus, ignoreCase: true, out var parsedStatus))
            {
                return (null, new RowError(
                    lineNumber,
                    sku.Value.Value,
                    $"Status '{rawStatus}' is not one of: "
                    + string.Join(", ", Enum.GetNames<ProductStatusValue>()) + "."));
            }

            status = parsedStatus;
        }

        var stock = ParseOptionalInt(csv, StockColumn, lineNumber, sku.Value.Value);

        if (stock.Error is not null)
        {
            return (null, stock.Error);
        }

        var threshold = ParseOptionalInt(csv, ThresholdColumn, lineNumber, sku.Value.Value);

        if (threshold.Error is not null)
        {
            return (null, threshold.Error);
        }

        var costPrice = ParseCostPrice(csv, lineNumber, sku.Value.Value);

        if (costPrice.Error is not null)
        {
            return (null, costPrice.Error);
        }

        var prices = new Dictionary<string, Money>(StringComparer.OrdinalIgnoreCase);

        foreach (var country in Country.All)
        {
            var column = $"Price_{country.Code}";
            var raw = Field(csv, column);

            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            {
                return (null, new RowError(
                    lineNumber,
                    sku.Value.Value,
                    $"{column} '{raw}' is not a valid number."));
            }

            // The currency is derived from the country, never taken from the file — so a price
            // can never be stored in a currency that country does not trade in.
            var money = Money.Create(amount, country.CurrencyCode);

            if (money.IsError)
            {
                return (null, new RowError(lineNumber, sku.Value.Value, $"{column}: {money.FirstError.Description}"));
            }

            if (money.Value.IsZero)
            {
                return (null, new RowError(lineNumber, sku.Value.Value, $"{column} must be greater than zero."));
            }

            prices[country.Code] = money.Value;
        }

        // Catch the impossible combination before anything is written, rather than letting the
        // aggregate reject it halfway through applying the file.
        if (status is ProductStatusValue.Active && prices.Count == 0)
        {
            return (null, new RowError(
                lineNumber,
                sku.Value.Value,
                "Status is Active but no price is set. A product cannot be activated without a price."));
        }

        return (new ParsedProductRow(
            lineNumber,
            sku.Value,
            name.Trim(),
            status,
            stock.Value,
            threshold.Value,
            costPrice.Value,
            prices), null);
    }

    private static (int? Value, RowError? Error) ParseOptionalInt(
        CsvReader csv,
        string column,
        int lineNumber,
        string sku)
    {
        var raw = Field(csv, column);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, null);
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return (null, new RowError(lineNumber, sku, $"{column} '{raw}' is not a whole number."));
        }

        return value < 0
            ? (null, new RowError(lineNumber, sku, $"{column} cannot be negative."))
            : (value, null);
    }

    private static (Money? Value, RowError? Error) ParseCostPrice(CsvReader csv, int lineNumber, string sku)
    {
        var raw = Field(csv, CostPriceColumn);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, null);
        }

        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            return (null, new RowError(lineNumber, sku, $"CostPrice '{raw}' is not a valid number."));
        }

        var currency = Field(csv, CostCurrencyColumn);

        if (string.IsNullOrWhiteSpace(currency))
        {
            return (null, new RowError(
                lineNumber,
                sku,
                "CostPriceCurrency is required when CostPrice is set — a bare amount with no "
                + "currency is exactly the ambiguity Onyx A43 shipped."));
        }

        var money = Money.Create(amount, currency);

        return money.IsError
            ? (null, new RowError(lineNumber, sku, $"CostPrice: {money.FirstError.Description}"))
            : (money.Value, null);
    }

    private static string? Field(CsvReader csv, string column) =>
        csv.TryGetField<string>(column, out var value) ? value : null;
}
