using BoomboCard.Catalog.Contracts;
using BoomboCard.Catalog.Data.Repositories;
using BoomboCard.Catalog.Data;
using BoomboCard.Catalog.Domain.ProductAggregate;
using BoomboCard.Catalog.Domain.StockMovements;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;

namespace BoomboCard.Catalog.Application.Imports;

public enum ImportRowOutcome
{
    Created = 0,
    Updated = 1,
    Unchanged = 2,
    Failed = 3
}

/// <summary>
/// What happened to one row. <c>Notes</c> carries anything that was deliberately not applied —
/// most often a stock column ignored on an update.
/// </summary>
public sealed record ImportRowResult(
    int LineNumber,
    string? Sku,
    ImportRowOutcome Outcome,
    string? Error,
    IReadOnlyList<string> Notes);

/// <summary>
/// The outcome of one import.
///
/// <c>Committed</c> is the field that matters: when it is false nothing was written, either
/// because the file had errors or because <c>dryRun</c> was set. A rejected import comes back as
/// 400 with this same body, so the per-row reasons are available either way.
/// </summary>
public sealed record ProductImportResult(
    int TotalRows,
    int Created,
    int Updated,
    int Unchanged,
    int Failed,
    bool Committed,
    IReadOnlyList<ImportRowResult> Rows);

/// <summary>
/// Imports a product CSV.
///
/// Three decisions worth defending:
///
/// <para>
/// <b>All or nothing.</b> The whole file is parsed and validated first; if any row is bad,
/// nothing is written. Onyx A35 called <c>SaveChangesAsync</c> inside its row loop, so a single
/// malformed cell threw on row 57 and left rows 1–56 committed — a half-loaded catalogue with no
/// record of where it stopped and no way to resume.
/// </para>
///
/// <para>
/// <b>Upsert on SKU.</b> Onyx A36 always did <c>new Product</c>, so re-importing a corrected
/// spreadsheet duplicated the entire catalogue instead of updating it — which is the whole point
/// of the export/edit/re-import loop it advertised.
/// </para>
///
/// <para>
/// <b>Stock is set on create only, never on update.</b> This is the one that surprises people.
/// Stock moves only through evented movements with a stated reason, so that
/// <c>SUM(Delta) == StockQuantity</c> holds. If an update overwrote stock from a spreadsheet, the
/// audit trail would no longer explain the number and the reconciliation invariant would break.
/// Stock changes through goods-in, a stocktake or a write-off — not a column in a file someone
/// edited. Rows where the column is ignored say so in their Notes.
/// </para>
/// </summary>
public sealed record ImportProductsCommand(
    byte[] Content,
    string RequestedBy,
    bool DryRun = false) : IRequest<ErrorOr<ProductImportResult>>, ICatalogRequest;

internal sealed class ImportProductsCommandHandler(
    IProductRepository products,
    IUnitOfWork<CatalogDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<ImportProductsCommand, ErrorOr<ProductImportResult>>
{
    public async Task<ErrorOr<ProductImportResult>> Handle(
        ImportProductsCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.RequestedBy))
        {
            return Error.Validation("Import.RequestedByRequired", "The requesting user is required.");
        }

        if (command.Content.Length == 0)
        {
            return Error.Validation("Import.EmptyFile", "The uploaded file is empty.");
        }

        // ---------------------------------------------------- phase 1: parse and validate

        using var stream = new MemoryStream(command.Content, writable: false);
        var parsed = await ProductCsvReader.ReadAsync(stream, cancellationToken);

        if (parsed.Errors.Count > 0)
        {
            // Every error at once, so one upload tells the user everything wrong with the file
            // rather than making them fix one row at a time.
            return new ProductImportResult(
                parsed.Rows.Count + parsed.Errors.Count,
                Created: 0,
                Updated: 0,
                Unchanged: 0,
                Failed: parsed.Errors.Count,
                Committed: false,
                parsed.Errors
                    .Select(error => new ImportRowResult(
                        error.LineNumber, error.Sku, ImportRowOutcome.Failed, error.Message, []))
                    .OrderBy(row => row.LineNumber)
                    .ToList());
        }

        // ---------------------------------------------------- phase 2: apply

        var nowUtc = timeProvider.GetUtcNow();

        // One query for every SKU in the file, not one per row. Onyx A26's N+1 shape.
        var skus = parsed.Rows.Select(row => row.Sku.Value).ToList();
        var existing = (await products.GetBySkusAsync(skus, cancellationToken))
            .ToDictionary(product => product.Sku.Value, StringComparer.OrdinalIgnoreCase);

        var results = new List<ImportRowResult>(parsed.Rows.Count);
        var movements = new List<StockMovement>();

        foreach (var row in parsed.Rows)
        {
            var applied = existing.TryGetValue(row.Sku.Value, out var product)
                ? ApplyUpdate(product!, row, nowUtc)
                : ApplyCreate(row, nowUtc, movements);

            if (applied.Error is not null)
            {
                // A domain rule rejected it. Abandon the whole import — the parse phase is for
                // data errors, this is for rule violations, and both mean the file is not safe
                // to apply.
                return new ProductImportResult(
                    parsed.Rows.Count,
                    Created: 0,
                    Updated: 0,
                    Unchanged: 0,
                    Failed: 1,
                    Committed: false,
                    [new ImportRowResult(row.LineNumber, row.Sku.Value, ImportRowOutcome.Failed, applied.Error, [])]);
            }

            results.Add(new ImportRowResult(
                row.LineNumber, row.Sku.Value, applied.Outcome, null, applied.Notes));
        }

        if (command.DryRun)
        {
            // Not saving is not enough. The aggregates are tracked and carry pending domain
            // events, and the dispatcher saves whatever the change tracker holds — so a dry run
            // would be committed by the dispatcher a moment later. Discard explicitly.
            unitOfWork.DiscardChanges();
        }
        else
        {
            products.AddMovements(movements);

            // ONE save for the whole file. UnitOfWorkBehavior holds the transaction around it and
            // drains domain events inside it, so every product, price, movement and outbox
            // message from this import commits together or not at all.
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return new ProductImportResult(
            parsed.Rows.Count,
            results.Count(row => row.Outcome is ImportRowOutcome.Created),
            results.Count(row => row.Outcome is ImportRowOutcome.Updated),
            results.Count(row => row.Outcome is ImportRowOutcome.Unchanged),
            Failed: 0,
            Committed: !command.DryRun,
            results);
    }

    private (ImportRowOutcome Outcome, string? Error, List<string> Notes) ApplyCreate(
        ParsedProductRow row,
        DateTimeOffset nowUtc,
        List<StockMovement> movements)
    {
        var notes = new List<string>();

        var stock = StockLevel.Create(row.StockQuantity ?? 0);

        if (stock.IsError)
        {
            return (ImportRowOutcome.Failed, stock.FirstError.Description, notes);
        }

        var product = Product.Create(
            row.Sku,
            row.Name,
            stock.Value,
            nowUtc,
            row.LowStockThreshold ?? Product.DefaultLowStockThreshold);

        products.Add(product);

        // Opening balance, so SUM(Delta) == StockQuantity holds from the first row.
        if (stock.Value.Quantity > 0)
        {
            movements.Add(StockMovement.Create(
                product.Id,
                stock.Value.Quantity,
                StockReason.Adjustment,
                StockReferences.OpeningBalance,
                stock.Value.Quantity,
                nowUtc));
        }

        var applied = ApplyPricesAndStatus(product, row, nowUtc, notes);

        return applied is not null
            ? (ImportRowOutcome.Failed, applied, notes)
            : (ImportRowOutcome.Created, null, notes);
    }

    private static (ImportRowOutcome Outcome, string? Error, List<string> Notes) ApplyUpdate(
        Product product,
        ParsedProductRow row,
        DateTimeOffset nowUtc)
    {
        var notes = new List<string>();
        var changed = false;

        if (product.Name != row.Name)
        {
            var renamed = product.Rename(row.Name, nowUtc);

            if (renamed.IsError)
            {
                return (ImportRowOutcome.Failed, renamed.FirstError.Description, notes);
            }

            changed = true;
        }

        // See the class remarks: stock is never overwritten from a file.
        if (row.StockQuantity is not null && row.StockQuantity != product.Stock.Quantity)
        {
            notes.Add(
                $"StockQuantity column ignored ({row.StockQuantity} in the file, "
                + $"{product.Stock.Quantity} on record). Stock changes through goods-in, a "
                + "stocktake or a write-off, so the movement log always explains the number.");
        }

        var pricesBefore = product.Prices.Count;
        var applied = ApplyPricesAndStatus(product, row, nowUtc, notes);

        if (applied is not null)
        {
            return (ImportRowOutcome.Failed, applied, notes);
        }

        changed |= product.Prices.Count != pricesBefore;

        return (changed ? ImportRowOutcome.Updated : ImportRowOutcome.Unchanged, null, notes);
    }

    private static string? ApplyPricesAndStatus(
        Product product,
        ParsedProductRow row,
        DateTimeOffset nowUtc,
        List<string> notes)
    {
        foreach (var (countryCode, amount) in row.PricesByCountry)
        {
            var country = Country.FromCode(countryCode);

            if (country.IsError)
            {
                return country.FirstError.Description;
            }

            var result = product.SetPrice(country.Value, amount, nowUtc, nowUtc);

            if (result.IsError)
            {
                return result.FirstError.Description;
            }
        }

        if (row.CostPrice is not null)
        {
            var cost = product.ChangeCostPrice(row.CostPrice, nowUtc);

            if (cost.IsError)
            {
                return cost.FirstError.Description;
            }
        }

        switch (row.Status)
        {
            case ProductStatusValue.Active when !product.Status.IsActive:
                var activated = product.Activate(nowUtc);

                if (activated.IsError)
                {
                    return activated.FirstError.Description;
                }

                break;

            case ProductStatusValue.Discontinued when !product.Status.IsDiscontinued:
                var discontinued = product.Discontinue(nowUtc);

                if (discontinued.IsError)
                {
                    return discontinued.FirstError.Description;
                }

                break;

            case ProductStatusValue.Draft when !product.Status.IsDraft:
                // There is no transition back to Draft. Say so rather than silently ignoring it.
                notes.Add(
                    $"Status column says Draft but the product is {product.Status}. "
                    + "There is no transition back to Draft; use Discontinued to withdraw it.");
                break;
        }

        return null;
    }
}
