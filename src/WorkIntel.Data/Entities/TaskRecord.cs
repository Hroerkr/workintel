using System;

namespace WorkIntel.Data.Entities;

/// <summary>
/// EF Core entity for a task row. Named <c>TaskRecord</c> rather than <c>Task</c>
/// to avoid namespace collision with <see cref="System.Threading.Tasks.Task"/>.
/// </summary>
/// <remarks>
/// The <see cref="UserId"/> column exists from day 1 so multi-user support is a
/// later auth-layer addition rather than a schema migration. In Phase 1 every
/// row is written with <c>UserId = "local"</c>.
/// </remarks>
public sealed class TaskRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "local";

    /// <summary>'audio' | 'slack' — mirrors the proto's TaskSource enum.</summary>
    public string Source { get; set; } = "audio";

    /// <summary>Stored as Postgres <c>jsonb</c>; consumer-defined schema.</summary>
    public string SourceMetaJson { get; set; } = "{}";

    public string OriginalText { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Owner { get; set; }
    public string? Deadline { get; set; }
    public double Confidence { get; set; }

    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

    /// <summary>'pending' | 'included' | 'excluded' | 'removed' | 'exported'.</summary>
    public string Status { get; set; } = "pending";

    public DateTime StatusAt { get; set; } = DateTime.UtcNow;

    public string? ExportTarget { get; set; }
    public DateTime? ExportedAt { get; set; }
}
