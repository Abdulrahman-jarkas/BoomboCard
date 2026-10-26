using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace BoomboCard.Api.OpenApi;

/// <summary>
/// Fills in the document-level metadata: what this API is, and the handful of behaviours a
/// consumer has to know about before calling it.
///
/// The tag descriptions carry the operational contract — idempotency keys, the 202-then-poll
/// shape, which operations are all-or-nothing. Onyx's OpenAPI document was the default with no
/// descriptions at all, while its README and PR description advertised features that either did
/// not exist (CSV import, A32) or did not behave as described (retry safety, M5).
/// </summary>
internal sealed class CatalogDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "BoomboCard API",
            Version = "v1",
            Description = """
                Catalogue and inventory for BoomboCard.

                ## Things worth knowing before you call this

                **Money is never accepted from the client.** Prices are set per country and the
                currency is derived from the country, so a price can never be stored in a currency
                that country does not trade in.

                **Prices are effective-dated and tax-exclusive.** Changing a price supersedes the
                old one rather than overwriting it, so `GET /products/{id}/price-history` returns
                every price the product has ever had. Tax is applied at checkout, not here.

                **A product cannot be activated without a price.** `POST /products/{id}/activate`
                returns `409` until at least one country has one. That is what makes "the price you
                see is the price you pay" structurally true rather than a promise.

                **Stock operations are idempotent on `reference`.** Sending the same
                `POST /stock/deduct` twice with the same reference is a no-op, not a double
                deduction — so retrying a failed checkout is safe. The reference is also the audit
                trail: every stock change records a movement with a stated reason, and
                `SUM(movements) == StockQuantity` is asserted by a background job.

                **Stock never increases on its own.** It moves through `/stock/receive` (goods-in),
                `/stock/adjust` (stocktake), `/stock/restore` (payment failed, session expired,
                cancellation, return) and `/stock/shrinkage` (write-off). Nothing invents stock.

                **Listings are keyset paged.** `GET /products` returns a `nextCursor`; pass it back
                as `cursor`. Offset paging would let rows shift between pages.

                **Exports are asynchronous.** `POST /exports` returns `202` with an id; poll
                `GET /exports/{id}` until `isDownloadable` is true. Downloads are addressed by that
                opaque id, never by filename.

                **Imports are all-or-nothing.** The whole file is validated first and every error
                is reported together; if any row fails, nothing is written. Pass `dryRun=true` to
                validate without writing.

                ## Errors

                Failures are RFC 9457 `application/problem+json` with the domain error code as the
                `title` — for example `StockLevel.Insufficient` or
                `Product.CannotActivateWithoutPrice`. A business failure never returns `200`.
                """,
            Contact = new OpenApiContact
            {
                Name = "BoomboCard engineering"
            }
        };

        OpenApiTag[] tags =
        [
            new OpenApiTag
            {
                Name = "Catalog",
                Description =
                    "Products: identity, naming, lifecycle and price per country. A product starts "
                    + "as Draft, needs a price before it can be activated, and can be discontinued "
                    + "to withdraw it from sale without destroying its history."
            },
            new OpenApiTag
            {
                Name = "Stock",
                Description =
                    "Every stock movement carries a reason and a reference, and the reference is "
                    + "the idempotency key. Overselling is prevented by an optimistic concurrency "
                    + "token, so a conflicting write loses and is retried rather than silently "
                    + "overwriting."
            },
            new OpenApiTag
            {
                Name = "Exports",
                Description =
                    "Request, poll, download. Exports have a lifecycle "
                    + "(Requested → Generating → Ready → Expired/Failed) and a retention window, "
                    + "so a failure is visible rather than a request that hangs."
            },
            new OpenApiTag
            {
                Name = "Imports",
                Description =
                    "Upload the same column layout the export produces, so export → edit → "
                    + "re-import is a real round trip. Upserts on SKU. The stock column is honoured "
                    + "on create and ignored on update, because stock moves through movements with "
                    + "a stated reason rather than a spreadsheet."
            },
            new OpenApiTag
            {
                Name = "Jobs",
                Description =
                    "Background job history: when each job last ran, whether it succeeded, how much "
                    + "it processed and how long it took. Persisted rows, so every instance gives "
                    + "the same answer."
            },
            new OpenApiTag
            {
                Name = "Health",
                Description = "Liveness."
            }
        ];

        document.Tags ??= new HashSet<OpenApiTag>();

        foreach (var tag in tags)
        {
            document.Tags.Add(tag);
        }

        return Task.CompletedTask;
    }
}
