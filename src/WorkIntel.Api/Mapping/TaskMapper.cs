using Google.Protobuf.WellKnownTypes;
using WorkIntel.Contracts.V1;
using EntityTask = WorkIntel.Data.Entities.TaskRecord;
using ProtoTask = WorkIntel.Contracts.V1.TaskItem;

namespace WorkIntel.Api.Mapping;

/// <summary>
/// Bidirectional mapping between the EF Core entity and the proto-generated
/// DTO. Lives in a single static class because mapping is mechanical and we
/// don't want a DI-dependency just to translate between two POCOs.
/// </summary>
internal static class TaskMapper
{
    public static ProtoTask ToProto(EntityTask e)
    {
        var p = new ProtoTask
        {
            Id = e.Id.ToString(),
            UserId = e.UserId,
            Source = SourceFromString(e.Source),
            SourceMetaJson = e.SourceMetaJson,
            OriginalText = e.OriginalText,
            Title = e.Title,
            Confidence = e.Confidence,
            DetectedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(e.DetectedAt, DateTimeKind.Utc)),
            Status = StatusFromString(e.Status),
            StatusAt = Timestamp.FromDateTime(DateTime.SpecifyKind(e.StatusAt, DateTimeKind.Utc)),
        };

        if (e.Description is not null) p.Description = e.Description;
        if (e.Owner is not null) p.Owner = e.Owner;
        if (e.Deadline is not null) p.Deadline = e.Deadline;
        if (e.ExportTarget is not null) p.ExportTarget = e.ExportTarget;
        if (e.ExportedAt is not null)
            p.ExportedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(e.ExportedAt.Value, DateTimeKind.Utc));

        return p;
    }

    public static EntityTask FromProto(ProtoTask p)
    {
        return new EntityTask
        {
            Id = !string.IsNullOrEmpty(p.Id) && Guid.TryParse(p.Id, out var g) ? g : Guid.Empty,
            UserId = string.IsNullOrWhiteSpace(p.UserId) ? "local" : p.UserId,
            Source = SourceToString(p.Source),
            SourceMetaJson = string.IsNullOrEmpty(p.SourceMetaJson) ? "{}" : p.SourceMetaJson,
            OriginalText = p.OriginalText,
            Title = p.Title,
            Description = p.HasDescription ? p.Description : null,
            Owner = p.HasOwner ? p.Owner : null,
            Deadline = p.HasDeadline ? p.Deadline : null,
            Confidence = p.Confidence,
            DetectedAt = p.DetectedAt?.ToDateTime() ?? DateTime.UtcNow,
            Status = StatusToString(p.Status),
            StatusAt = p.StatusAt?.ToDateTime() ?? DateTime.UtcNow,
            ExportTarget = p.HasExportTarget ? p.ExportTarget : null,
            ExportedAt = p.ExportedAt?.ToDateTime(),
        };
    }

    public static string SourceToString(TaskSource s) => s switch
    {
        TaskSource.Audio => "audio",
        TaskSource.Slack => "slack",
        _ => "audio",
    };

    public static TaskSource SourceFromString(string s) => s switch
    {
        "audio" => TaskSource.Audio,
        "slack" => TaskSource.Slack,
        _ => TaskSource.Unspecified,
    };

    public static string StatusToString(WorkIntel.Contracts.V1.TaskStatus s) => s switch
    {
        WorkIntel.Contracts.V1.TaskStatus.Pending  => "pending",
        WorkIntel.Contracts.V1.TaskStatus.Included => "included",
        WorkIntel.Contracts.V1.TaskStatus.Excluded => "excluded",
        WorkIntel.Contracts.V1.TaskStatus.Removed  => "removed",
        WorkIntel.Contracts.V1.TaskStatus.Exported => "exported",
        _ => "pending",
    };

    public static WorkIntel.Contracts.V1.TaskStatus StatusFromString(string s) => s switch
    {
        "pending"  => WorkIntel.Contracts.V1.TaskStatus.Pending,
        "included" => WorkIntel.Contracts.V1.TaskStatus.Included,
        "excluded" => WorkIntel.Contracts.V1.TaskStatus.Excluded,
        "removed"  => WorkIntel.Contracts.V1.TaskStatus.Removed,
        "exported" => WorkIntel.Contracts.V1.TaskStatus.Exported,
        _ => WorkIntel.Contracts.V1.TaskStatus.Unspecified,
    };
}
