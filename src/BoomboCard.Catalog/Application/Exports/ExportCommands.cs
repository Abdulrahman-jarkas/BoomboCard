using BoomboCard.Catalog.Contracts;
using BoomboCard.Catalog.Data.Repositories;
using BoomboCard.Catalog.Data;
using BoomboCard.Catalog.Domain.Exports;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;

namespace BoomboCard.Catalog.Application.Exports;

/// <summary>
/// Returns immediately with an id. Generation happens in the background.
///
/// Onyx generated the file inline inside the request. Fine for five products; a timeout for
/// fifty thousand. Separating the request from the work also means the client has something to
/// poll and something to retry.
/// </summary>
public sealed record RequestProductExportCommand(string RequestedBy)
    : IRequest<ErrorOr<Guid>>, ICatalogRequest;

/// <summary>
/// The state of one export. Poll until <c>IsDownloadable</c> is true, then fetch the download.
/// A failure carries its reason in <c>Error</c> rather than sitting in Generating forever.
/// </summary>
public sealed record ProductExportResponse(
    Guid ExportId,
    string Status,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string? FileName,
    int? RowCount,
    string? Error,
    bool IsDownloadable);

public sealed record GetProductExportQuery(Guid ExportId) : IRequest<ErrorOr<ProductExportResponse>>;

internal sealed class RequestProductExportCommandHandler(
    IProductExportRepository exports,
    IUnitOfWork<CatalogDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<RequestProductExportCommand, ErrorOr<Guid>>
{
    public async Task<ErrorOr<Guid>> Handle(
        RequestProductExportCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.RequestedBy))
        {
            return Error.Validation("Export.RequestedByRequired", "The requesting user is required.");
        }

        var export = ProductExport.Request(command.RequestedBy.Trim(), timeProvider.GetUtcNow());

        exports.Add(export);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return export.Id;
    }
}

internal sealed class GetProductExportQueryHandler(
    IProductExportRepository exports,
    TimeProvider timeProvider)
    : IRequestHandler<GetProductExportQuery, ErrorOr<ProductExportResponse>>
{
    public async Task<ErrorOr<ProductExportResponse>> Handle(
        GetProductExportQuery query,
        CancellationToken cancellationToken)
    {
        var export = await exports.GetByIdAsync(query.ExportId, cancellationToken);

        if (export is null)
        {
            return ExportErrors.NotFound(query.ExportId);
        }

        return new ProductExportResponse(
            export.Id,
            export.Status.ToString(),
            export.RequestedAtUtc,
            export.CompletedAtUtc,
            export.ExpiresAtUtc,
            export.FileName,
            export.RowCount,
            export.Error,
            export.IsDownloadable(timeProvider.GetUtcNow()));
    }
}
