using BoomboCard.Catalog.Domain.Exports.Events;
using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Catalog.Domain.Exports;

public enum ExportStatus
{
    Requested = 0,
    Generating = 1,
    Ready = 2,
    Failed = 3,
    Expired = 4
}

/// <summary>
/// A catalogue export, modelled as a record with a lifecycle rather than a file.
///
/// Onyx's export was a synchronous write to <c>AppContext.BaseDirectory/exports</c> returning a
/// filename, and a second endpoint that read it back by that filename. Five problems, all of
/// which this shape fixes:
///
///  - <b>S6, path traversal.</b> <c>Path.Combine(dir, fileName)</c> with the raw query string,
///    so <c>?fileName=../../appsettings.json</c> returned the file holding the payment key, the
///    production database password and the JWT signing key. Here the client holds an opaque
///    <see cref="Entity.Id"/>; the path is server-owned and never supplied by a caller.
///  - <b>A38, collisions.</b> Its filename was <c>products-{yyyyMMddHHmmss}.csv</c>, so two
///    exports in the same second overwrote each other. The id is the identity now.
///  - <b>A38, unbounded growth.</b> Files accumulated forever inside the deployment directory —
///    which is also wiped on redeploy, invalidating any filename a client was holding.
///    <see cref="ExpiresAtUtc"/> gives a reaper something to act on.
///  - <b>No status.</b> Generation was inline, so a slow export was a slow request. Here the
///    client gets an id immediately and polls.
///  - <b>No failure state.</b> An export that threw left nothing behind.
/// </summary>
public sealed class ProductExport : AggregateRoot
{
    public const int RetentionDays = 7;

    public ExportStatus Status { get; private set; }

    public DateTimeOffset RequestedAtUtc { get; private set; }

    public string RequestedBy { get; private set; } = string.Empty;

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public DateTimeOffset? ExpiresAtUtc { get; private set; }

    /// <summary>The name offered to the browser. Not a path, and never used to resolve one.</summary>
    public string? FileName { get; private set; }

    /// <summary>Server-owned storage location. Never accepted from a caller.</summary>
    public string? StoragePath { get; private set; }

    public int? RowCount { get; private set; }

    public string? Error { get; private set; }

    private ProductExport() { }

    public static ProductExport Request(string requestedBy, DateTimeOffset nowUtc)
    {
        var export = new ProductExport
        {
            Id = Guid.NewGuid(),
            Status = ExportStatus.Requested,
            RequestedAtUtc = nowUtc,
            RequestedBy = requestedBy
        };

        export.RaiseDomainEvent(new ProductExportRequestedEvent(export.Id, requestedBy, nowUtc));

        return export;
    }

    /// <summary>
    /// Claimed by a generator. The transition from Requested is what stops two generators
    /// picking up the same export — the Version token makes the claim atomic.
    /// </summary>
    public ErrorOr<Success> BeginGenerating(DateTimeOffset nowUtc)
    {
        if (Status is not ExportStatus.Requested)
        {
            return ExportErrors.NotRequested(Id, Status);
        }

        Status = ExportStatus.Generating;

        RaiseDomainEvent(new ProductExportStartedEvent(Id, nowUtc));

        return Result.Success;
    }

    public ErrorOr<Success> Complete(string fileName, string storagePath, int rowCount, DateTimeOffset nowUtc)
    {
        if (Status is not ExportStatus.Generating)
        {
            return ExportErrors.NotGenerating(Id, Status);
        }

        Status = ExportStatus.Ready;
        FileName = fileName;
        StoragePath = storagePath;
        RowCount = rowCount;
        CompletedAtUtc = nowUtc;
        ExpiresAtUtc = nowUtc.AddDays(RetentionDays);

        RaiseDomainEvent(new ProductExportCompletedEvent(Id, fileName, rowCount, nowUtc));

        return Result.Success;
    }

    public ErrorOr<Success> Fail(string error, DateTimeOffset nowUtc)
    {
        if (Status is ExportStatus.Ready or ExportStatus.Expired)
        {
            return ExportErrors.AlreadyFinished(Id, Status);
        }

        Status = ExportStatus.Failed;
        Error = error.Length > 2000 ? error[..2000] : error;
        CompletedAtUtc = nowUtc;

        RaiseDomainEvent(new ProductExportFailedEvent(Id, Error, nowUtc));

        return Result.Success;
    }

    public ErrorOr<Success> Expire(DateTimeOffset nowUtc)
    {
        if (Status is not ExportStatus.Ready)
        {
            return ExportErrors.NotReady(Id, Status);
        }

        Status = ExportStatus.Expired;

        RaiseDomainEvent(new ProductExportExpiredEvent(Id, StoragePath, nowUtc));

        return Result.Success;
    }

    public bool IsDownloadable(DateTimeOffset at) =>
        Status is ExportStatus.Ready
        && StoragePath is not null
        && (ExpiresAtUtc is null || ExpiresAtUtc > at);
}

public static class ExportErrors
{
    public static Error NotFound(Guid exportId) =>
        Error.NotFound("Export.NotFound", $"Export '{exportId}' was not found.");

    public static Error NotRequested(Guid exportId, ExportStatus status) =>
        Error.Conflict("Export.NotRequested", $"Export '{exportId}' is {status}, not Requested.");

    public static Error NotGenerating(Guid exportId, ExportStatus status) =>
        Error.Conflict("Export.NotGenerating", $"Export '{exportId}' is {status}, not Generating.");

    public static Error NotReady(Guid exportId, ExportStatus status) =>
        Error.Conflict("Export.NotReady", $"Export '{exportId}' is {status}, not Ready.");

    public static Error AlreadyFinished(Guid exportId, ExportStatus status) =>
        Error.Conflict("Export.AlreadyFinished", $"Export '{exportId}' is already {status}.");

    public static Error NotDownloadable(Guid exportId, ExportStatus status) =>
        Error.Conflict(
            "Export.NotDownloadable",
            $"Export '{exportId}' is {status} and cannot be downloaded.");
}
