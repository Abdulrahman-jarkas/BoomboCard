using BoomboCard.Catalog.Application.Exports;
using BoomboCard.Catalog.Application.Imports;
using BoomboCard.Catalog.Application.Jobs;
using BoomboCard.Catalog.Application.Products.Commands;
using BoomboCard.Catalog.Application.Products.Queries;
using BoomboCard.Catalog.Contracts;
using BoomboCard.Catalog.Data.Repositories;
using BoomboCard.Catalog.Domain.Exports;
using BoomboCard.Catalog.Domain.ProductAggregate;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace BoomboCard.Catalog.Endpoints;

/// <summary>
/// The module owns its own routes. The host calls MapCatalogEndpoints and knows nothing about
/// what is inside.
///
/// Two rules every endpoint here follows:
///
///  1. <b>Commands go through ICommandDispatcher, queries through ISender.</b> The dispatcher
///     adds the concurrency retry in a fresh scope; a query needs neither.
///  2. <b>Errors map to real status codes.</b> Onyx A23 returned
///     <c>Ok(new { error = "Not enough stock for X" })</c> — HTTP 200 with a failure inside it,
///     so any client checking the status code treated a rejected order as a completed one.
/// </summary>
public static class CatalogEndpoints
{
    private const long MaxImportBytes = 10 * 1024 * 1024;

    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var products = app.MapGroup("/api/catalog/products").WithTags("Catalog");
        var stock = app.MapGroup("/api/catalog/stock").WithTags("Stock");
        var exports = app.MapGroup("/api/catalog/exports").WithTags("Exports");
        var imports = app.MapGroup("/api/catalog/imports").WithTags("Imports");
        var jobs = app.MapGroup("/api/catalog/jobs").WithTags("Jobs");

        MapProductEndpoints(products);
        MapStockEndpoints(stock);
        MapExportEndpoints(exports);
        MapImportEndpoints(imports);
        MapJobEndpoints(jobs);

        return app;
    }

    private static void MapProductEndpoints(RouteGroupBuilder products)
    {
        products.MapPost("/", async (
            CreateProductCommand command,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(command, cancellationToken);

            return result.Match(
                productId => Results.Created(
                    $"/api/catalog/products/{productId}",
                    new CreatedProductResponse(productId)),
                ToProblem);
        })
        .WithName("CreateProduct")
        .WithSummary("Create a product. It starts as Draft and cannot be sold until it has a price.")
        .WithDescription(
            "The SKU is normalised to upper case, so ' + Q + 'onx-001' + Q + ' and ' + Q + 'ONX-001' + Q + ' are the same "
            + "product and the second one is a 409. An initial stock quantity records an "
            + "opening-balance movement, so the audit log explains the number from the first row.")
        .Produces<CreatedProductResponse>(StatusCodes.Status201Created)
        .WithWriteProblems();

        products.MapGet("/{productId:guid}", async (
            Guid productId,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.Send(new GetProductQuery(productId), cancellationToken);
            return result.Match(Results.Ok, ToProblem);
        })
        .WithName("GetProduct")
        .WithSummary("Full detail for one product, including every price and its validity window.")
        .Produces<ProductDetailResponse>()
        .WithReadProblems();

        // Keyset paging: pass the previous page's last SKU back as `cursor`.
        products.MapGet("/", async (
            ISender sender,
            CancellationToken cancellationToken,
            [FromQuery] string? search = null,
            [FromQuery] ProductStatusValue? status = null,
            [FromQuery] bool lowStockOnly = false,
            [FromQuery] string? cursor = null,
            [FromQuery] int limit = 50) =>
        {
            var result = await sender.Send(
                new SearchProductsQuery(search, status, lowStockOnly, cursor, limit),
                cancellationToken);

            return result.Match(Results.Ok, ToProblem);
        })
        .WithName("SearchProducts")
        .WithSummary("Search the catalogue. Keyset paged.")
        .WithDescription(
            "Keyset paged, not offset paged: the response carries `nextCursor`, the last SKU on "
            + "the page. Pass it back as `cursor` for the next page. Because SKU is unique and "
            + "ordered, no row can be seen twice or skipped however much the table changes "
            + "between requests. `limit` is capped at 200.")
        .Produces<ProductPage>()
        .ProducesProblem(StatusCodes.Status400BadRequest);

        products.MapGet("/{productId:guid}/price-history", async (
            Guid productId,
            ISender sender,
            CancellationToken cancellationToken,
            [FromQuery] string? countryCode = null) =>
        {
            var result = await sender.Send(
                new GetPriceHistoryQuery(productId, countryCode),
                cancellationToken);

            return result.Match(Results.Ok, ToProblem);
        })
        .WithName("GetPriceHistory")
        .WithSummary("Every price this product has ever had.")
        .WithDescription(
            "Readable only because a price change closes the old row and opens a new one rather "
            + "than overwriting it. `isCurrent` marks the one effective right now; a future-dated "
            + "price appears with `isCurrent: false` until it takes effect.")
        .Produces<List<PriceHistoryEntry>>()
        .WithReadProblems();

        products.MapPut("/{productId:guid}/name", async (
            Guid productId,
            RenameProductRequest request,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new RenameProductCommand(productId, request.Name),
                cancellationToken);

            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .WithName("RenameProduct")
        .WithSummary("Change the display name. Renaming to the same value is a no-op.")
        .Produces(StatusCodes.Status204NoContent)
        .WithWriteProblems();

        products.MapPut("/{productId:guid}/prices/{countryCode}", async (
            Guid productId,
            string countryCode,
            SetPriceRequest request,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new SetProductPriceCommand(productId, countryCode, request.Amount, request.EffectiveFrom),
                cancellationToken);

            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .WithName("SetProductPrice")
        .WithSummary("Set or change the price for one country.")
        .WithDescription(
            "The currency comes from the country, never from the request, so a price cannot be "
            + "stored in a currency that country does not trade in. `effectiveFrom` may be in the "
            + "future, which is how a scheduled price change works with no midnight job. Setting "
            + "a price supersedes the current one rather than overwriting it.")
        .Produces(StatusCodes.Status204NoContent)
        .WithWriteProblems();

        products.MapDelete("/{productId:guid}/prices/{countryCode}", async (
            Guid productId,
            string countryCode,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new WithdrawProductPriceCommand(productId, countryCode),
                cancellationToken);

            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .WithName("WithdrawProductPrice")
        .WithSummary("Stop selling in one country without discontinuing the product everywhere.")
        .WithDescription("Returns 409 if it would leave an active product with no price at all.")
        .Produces(StatusCodes.Status204NoContent)
        .WithWriteProblems();

        products.MapPut("/{productId:guid}/cost-price", async (
            Guid productId,
            SetCostPriceRequest request,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new SetProductCostPriceCommand(productId, request.CountryCode, request.Amount),
                cancellationToken);

            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .WithName("SetProductCostPrice")
        .WithSummary("What the product costs us, which is what makes margin knowable.")
        .Produces(StatusCodes.Status204NoContent)
        .WithWriteProblems();

        products.MapPost("/{productId:guid}/activate", async (
            Guid productId,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(new ActivateProductCommand(productId), cancellationToken);
            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .WithName("ActivateProduct")
        .WithSummary("Make the product sellable.")
        .WithDescription(
            "Returns 409 `Product.CannotActivateWithoutPrice` until at least one country has a "
            + "price. That is the invariant behind the promise that the price a customer sees is "
            + "the price they pay: a product with no price can never reach a basket.")
        .Produces(StatusCodes.Status204NoContent)
        .WithWriteProblems();

        products.MapPost("/{productId:guid}/discontinue", async (
            Guid productId,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(new DiscontinueProductCommand(productId), cancellationToken);
            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .WithName("DiscontinueProduct")
        .WithSummary("Withdraw from sale.")
        .WithDescription(
            "Stops new sales. Restores and returns still work on a discontinued product so "
            + "inventory can reconcile; only deductions require Active.")
        .Produces(StatusCodes.Status204NoContent)
        .WithWriteProblems();
    }

    private static void MapStockEndpoints(RouteGroupBuilder stock)
    {
        stock.MapPost("/check", async (
            BulkStockCheckQuery query,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.Send(query, cancellationToken);
            return result.Match(Results.Ok, ToProblem);
        })
        .WithName("BulkStockCheck")
        .WithSummary("Can we cover these quantities? Changes nothing.")
        .WithDescription(
            "`canFulfil` uses one availability rule: available >= requested, so exactly enough IS "
            + "enough. A product that does not exist comes back with `exists: false` rather than "
            + "reading as plentiful.")
        .Produces<List<StockCheckResult>>()
        .ProducesProblem(StatusCodes.Status400BadRequest);

        stock.MapPost("/deduct", async (
            DeductStockCommand command,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(command, cancellationToken);
            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .WithName("DeductStock")
        .WithSummary("Take stock off the shelf for a checkout. Idempotent on reference.")
        .WithDescription(
            "All lines are applied in one transaction, so a short line cannot leave earlier "
            + "deductions committed. Idempotent on `reference` (the checkout session id), so "
            + "resending the same request is a no-op rather than a double deduction — which makes "
            + "retrying a failed checkout the cheapest recovery path. Returns 409 "
            + "`StockLevel.Insufficient` when a line cannot be covered.")
        .Produces(StatusCodes.Status204NoContent)
        .WithWriteProblems();

        stock.MapPost("/restore", async (
            RestoreStockCommand command,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(command, cancellationToken);
            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .WithName("RestoreStock")
        .WithSummary("Put stock back after a checkout did not complete.")
        .WithDescription(
            "Idempotent on `reference` plus `reason`, because one session legitimately produces "
            + "both a sale and a restore for the same product. Normally driven by an integration "
            + "event — payment failed, session expired, order cancelled — rather than called "
            + "directly.")
        .Produces(StatusCodes.Status204NoContent)
        .WithWriteProblems();

        stock.MapPost("/receive", async (
            ReceiveStockCommand command,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(command, cancellationToken);
            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .WithName("ReceiveStock")
        .WithSummary("Goods-in against a real delivery.")
        .WithDescription(
            "Records a receipt against a purchase order reference. There is no automatic "
            + "replenishment anywhere in this system: stock increases only when someone says it "
            + "arrived.")
        .Produces(StatusCodes.Status204NoContent)
        .WithWriteProblems();

        stock.MapPost("/adjust", async (
            AdjustStockCommand command,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(command, cancellationToken);
            return result.Match(Results.Ok, ToProblem);
        })
        .WithName("AdjustStock")
        .WithSummary("Stocktake. The counted figure wins and the discrepancy is audited.")
        .WithDescription(
            "The counted quantity becomes the recorded quantity and the difference comes back as "
            + "`delta`, which is the number worth investigating. A count that matches still "
            + "records a movement, because a confirmed count is itself worth knowing.")
        .Produces<StockAdjustmentResult>()
        .WithWriteProblems();

        stock.MapPost("/shrinkage", async (
            RecordShrinkageCommand command,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(command, cancellationToken);
            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .WithName("RecordShrinkage")
        .WithSummary("Write stock off as lost, broken or stolen.")
        .WithDescription(
            "Separate from a sale because no money came in, and separate from a stocktake because "
            + "the cause is known. Allowed on a discontinued product.")
        .Produces(StatusCodes.Status204NoContent)
        .WithWriteProblems();
    }

    private static void MapExportEndpoints(RouteGroupBuilder exports)
    {
        exports.MapPost("/", async (
            RequestProductExportCommand command,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(command, cancellationToken);

            return result.Match(
                exportId => Results.Accepted(
                    $"/api/catalog/exports/{exportId}",
                    new RequestedExportResponse(exportId)),
                ToProblem);
        })
        .WithName("RequestProductExport")
        .WithSummary("Request a catalogue export. Returns 202 with an id.")
        .WithDescription(
            "Returns immediately, so a large catalogue does not become a slow request. Poll "
            + "`GET /exports/{exportId}` until `isDownloadable` is true, then download. The "
            + "response describes something already committed: the export record exists before "
            + "you are told about it.")
        .Produces<RequestedExportResponse>(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        exports.MapGet("/{exportId:guid}", async (
            Guid exportId,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.Send(new GetProductExportQuery(exportId), cancellationToken);
            return result.Match(Results.Ok, ToProblem);
        })
        .WithName("GetProductExport")
        .WithSummary("Export status. Poll until isDownloadable is true.")
        .WithDescription(
            "A failed export carries the reason in `error` rather than sitting in Generating "
            + "forever, and an abandoned one is failed by a maintenance job so it becomes visible.")
        .Produces<ProductExportResponse>()
        .WithReadProblems();

        // The download. Addressed by OPAQUE ID, never by filename.
        //
        // Onyx S6 took the filename from the query string and passed it to Path.Combine, so
        // ?fileName=../../appsettings.json returned the file holding the payment gateway key,
        // the production database password and the JWT signing key. Here the caller supplies a
        // Guid; the path comes from the record we looked up, and never from the request.
        exports.MapGet("/{exportId:guid}/download", async (
            Guid exportId,
            IProductExportRepository repository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var export = await repository.GetByIdAsync(exportId, cancellationToken);

            if (export is null)
            {
                return ToProblem([ExportErrors.NotFound(exportId)]);
            }

            if (!export.IsDownloadable(timeProvider.GetUtcNow()))
            {
                return ToProblem([ExportErrors.NotDownloadable(exportId, export.Status)]);
            }

            if (!File.Exists(export.StoragePath))
            {
                return ToProblem([Error.NotFound(
                    "Export.FileMissing",
                    $"Export '{exportId}' is marked Ready but its file is gone.")]);
            }

            // The framework owns disposal of this stream. Onyx A37 read the whole file into a
            // byte[] via an undisposed FileStream, ignoring Read's return value (CA2022).
            var stream = File.OpenRead(export.StoragePath);

            return Results.File(stream, "text/csv", export.FileName);
        })
        .WithName("DownloadProductExport")
        .WithSummary("Download the generated CSV.")
        .WithDescription(
            "Addressed by the opaque export id, never by filename: the storage path is "
            + "server-owned and no part of it is accepted from the request. Returns 409 if the "
            + "export is not Ready, or has expired.")
        .Produces(StatusCodes.Status200OK, contentType: "text/csv")
        .WithReadProblems();
    }

    private static void MapImportEndpoints(RouteGroupBuilder imports)
    {
        // Multipart upload, not a server path. Onyx's importer took a `filePath` string, which
        // is the same path-traversal shape as its export download (S6) waiting to happen.
        imports.MapPost("/products", async (
            IFormFile file,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken,
            [FromQuery] string requestedBy = "unknown",
            [FromQuery] bool dryRun = false) =>
        {
            if (file.Length == 0)
            {
                return Results.Problem(
                    title: "Import.EmptyFile",
                    detail: "The uploaded file is empty.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (file.Length > MaxImportBytes)
            {
                return Results.Problem(
                    title: "Import.FileTooLarge",
                    detail: $"The file exceeds {MaxImportBytes / 1024 / 1024} MB.",
                    statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, cancellationToken);

            var result = await dispatcher.SendAsync(
                new ImportProductsCommand(buffer.ToArray(), requestedBy, dryRun),
                cancellationToken);

            return result.Match(
                import => import.Committed || import.Failed == 0
                    ? Results.Ok(import)
                    // Nothing was written, so this is a rejection, not a success. Onyx A23
                    // returned 200 with an error object inside it, so any client checking the
                    // status code treated a rejected request as a completed one.
                    : Results.BadRequest(import),
                ToProblem);
        })
        .WithName("ImportProducts")
        .WithSummary("Upload a product CSV. All-or-nothing.")
        .WithDescription(
            "Expects the same column layout the export produces, so export then edit then "
            + "re-import is a real round trip:\n\n"
            + "`Sku,Name,Status,StockQuantity,LowStockThreshold,CostPrice,CostPriceCurrency,"
            + "Price_US,Price_AE`\n\n"
            + "Only `Sku` and `Name` are required. The whole file is validated first and every "
            + "error is reported together; if any row fails, nothing is written and the response "
            + "is 400 carrying the per-row reasons. Pass `dryRun=true` to validate without "
            + "writing.\n\n"
            + "Upserts on SKU, so re-importing a corrected file updates rather than duplicates. "
            + "The stock column is honoured when CREATING a product and ignored when UPDATING "
            + "one: stock moves through goods-in, a stocktake or a write-off so the movement log "
            + "always explains the number. Rows where it is ignored say so in their notes.")
        .Accepts<IFormFile>("multipart/form-data")
        .Produces<ProductImportResult>()
        .Produces<ProductImportResult>(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
        .DisableAntiforgery();
    }

    private static void MapJobEndpoints(RouteGroupBuilder jobs)
    {
        jobs.MapGet("/runs", async (
            ISender sender,
            CancellationToken cancellationToken,
            [FromQuery] string? jobName = null,
            [FromQuery] int limit = 20) =>
        {
            var result = await sender.Send(new GetJobRunsQuery(jobName, limit), cancellationToken);
            return result.Match(Results.Ok, ToProblem);
        })
        .WithName("GetJobRuns")
        .WithSummary("Background job history: what ran, when, and whether it worked.")
        .WithDescription(
            "Persisted rows, so every instance gives the same answer. A run row is written "
            + "BEFORE the work starts, so a job that dies mid-pass leaves one stuck in `Running` "
            + "rather than leaving no trace at all.\n\n"
            + "`itemsFailed` on `catalog.stock-integrity-check` is the number of products whose "
            + "movement log does not sum to their recorded quantity. It should always be zero; "
            + "anything else is a defect worth paging someone about.\n\n"
            + "Job names: `catalog.stock-integrity-check`, `catalog.export-maintenance`.")
        .Produces<List<JobRunResponse>>()
        .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    // ----------------------------------------------------------------- OpenAPI metadata

    /// <summary>
    /// The problem responses every write endpoint can return. Declared once rather than on twenty
    /// endpoints, so the generated document shows real status codes and the ProblemDetails schema
    /// instead of a single undocumented 200.
    /// </summary>
    private static RouteHandlerBuilder WithWriteProblems(this RouteHandlerBuilder builder) =>
        builder
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    private static RouteHandlerBuilder WithReadProblems(this RouteHandlerBuilder builder) =>
        builder
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

    // ----------------------------------------------------------------- error mapping

    /// <summary>
    /// One mapper, so every endpoint returns the same shape. Onyx returned 404 from some paths,
    /// 200-with-an-error-body from others, and raw stack traces from a global handler that was
    /// not guarded by IsDevelopment (S10, A23).
    /// </summary>
    private static IResult ToProblem(List<Error> errors)
    {
        var first = errors[0];

        var statusCode = first.Type switch
        {
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status500InternalServerError
        };

        return Results.Problem(
            title: first.Code,
            detail: first.Description,
            statusCode: statusCode,
            extensions: errors.Count > 1
                ? new Dictionary<string, object?>
                {
                    ["errors"] = errors.Select(error => new { error.Code, error.Description }).ToArray()
                }
                : null);
    }
}

/// <summary>The id of the newly created product.</summary>
public sealed record CreatedProductResponse(Guid ProductId);

/// <summary>
/// The id of the requested export. Poll <c>GET /api/catalog/exports/{exportId}</c> with it.
/// </summary>
public sealed record RequestedExportResponse(Guid ExportId);

/// <summary>A new display name for the product.</summary>
public sealed record RenameProductRequest(string Name);

/// <summary>
/// A price for one country. The currency is derived from the country in the route, never supplied
/// here. <paramref name="EffectiveFrom"/> may be in the future to schedule a change; it defaults
/// to now.
/// </summary>
public sealed record SetPriceRequest(decimal Amount, DateTimeOffset? EffectiveFrom = null);

/// <summary>
/// What the product costs us. The country is used only to derive the currency, so a cost can
/// never be stored in a currency we do not trade in.
/// </summary>
public sealed record SetCostPriceRequest(string CountryCode, decimal Amount);
