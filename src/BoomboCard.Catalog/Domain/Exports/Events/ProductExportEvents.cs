using BoomboCard.SharedKernel;

namespace BoomboCard.Catalog.Domain.Exports.Events;

/*
 * One event per transition, as everywhere else — including the ones nothing listens to yet.
 * ProductExportExpiredEvent carries the StoragePath specifically so a future reaper can delete
 * the file without having to re-read the aggregate.
 */

public sealed record ProductExportRequestedEvent(
    Guid ExportId,
    string RequestedBy,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record ProductExportStartedEvent(
    Guid ExportId,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record ProductExportCompletedEvent(
    Guid ExportId,
    string FileName,
    int RowCount,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record ProductExportFailedEvent(
    Guid ExportId,
    string Error,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record ProductExportExpiredEvent(
    Guid ExportId,
    string? StoragePath,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;
