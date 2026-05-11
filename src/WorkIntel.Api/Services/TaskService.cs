using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using WorkIntel.Api.Mapping;
using WorkIntel.Contracts.V1;
using WorkIntel.Data;
using GrpcTaskService = WorkIntel.Contracts.V1.TaskService;
using ProtoTaskStatus = WorkIntel.Contracts.V1.TaskStatus;

namespace WorkIntel.Api.Services;

/// <summary>
/// gRPC implementation of <see cref="GrpcTaskService"/>. CRUD over the
/// <c>tasks</c> table; every successful mutation publishes a
/// <see cref="TaskEvent"/> to <see cref="TaskEventBus"/> for live UI updates.
/// </summary>
public sealed class TaskService : GrpcTaskService.TaskServiceBase
{
    private readonly WorkIntelDbContext _db;
    private readonly TaskEventBus _bus;
    private readonly ILogger<TaskService> _log;

    public TaskService(WorkIntelDbContext db, TaskEventBus bus, ILogger<TaskService> log)
    {
        _db = db;
        _bus = bus;
        _log = log;
    }

    public override async Task<TaskItem> CreateTask(CreateTaskRequest req, ServerCallContext context)
    {
        if (req.Task is null) throw new RpcException(new Status(StatusCode.InvalidArgument, "task is required"));
        if (string.IsNullOrWhiteSpace(req.Task.Title))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "title is required"));

        var entity = TaskMapper.FromProto(req.Task);
        if (entity.Id == Guid.Empty) entity.Id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        entity.DetectedAt = entity.DetectedAt == default ? now : entity.DetectedAt;
        entity.StatusAt = entity.StatusAt == default ? now : entity.StatusAt;
        if (string.IsNullOrEmpty(entity.Status)) entity.Status = "pending";

        _db.Tasks.Add(entity);
        await _db.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);

        _log.LogInformation("Task {Id} created (status={Status}, source={Source})", entity.Id, entity.Status, entity.Source);

        var proto = TaskMapper.ToProto(entity);
        Publish(TaskEventType.Created, proto);
        return proto;
    }

    public override async Task<TaskItem> GetTask(GetTaskRequest req, ServerCallContext context)
    {
        if (!Guid.TryParse(req.Id, out var id))
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"invalid id: {req.Id}"));

        var entity = await _db.Tasks.FindAsync(new object?[] { id }, context.CancellationToken).ConfigureAwait(false);
        if (entity is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"task {req.Id} not found"));

        return TaskMapper.ToProto(entity);
    }

    public override async Task<ListTasksResponse> ListTasks(ListTasksRequest req, ServerCallContext context)
    {
        IQueryable<Data.Entities.TaskRecord> q = _db.Tasks.AsNoTracking();

        if (req.HasUserId)
            q = q.Where(t => t.UserId == req.UserId);

        if (req.HasStatus && req.Status != ProtoTaskStatus.Unspecified)
        {
            var statusStr = TaskMapper.StatusToString(req.Status);
            q = q.Where(t => t.Status == statusStr);
        }

        int pageSize = req.HasPageSize && req.PageSize > 0 ? req.PageSize : 50;
        pageSize = Math.Clamp(pageSize, 1, 500);

        var rows = await q
            .OrderByDescending(t => t.DetectedAt)
            .Take(pageSize)
            .ToListAsync(context.CancellationToken).ConfigureAwait(false);

        var resp = new ListTasksResponse();
        foreach (var r in rows) resp.Tasks.Add(TaskMapper.ToProto(r));
        return resp;
    }

    public override async Task<TaskItem> UpdateTaskStatus(UpdateTaskStatusRequest req, ServerCallContext context)
    {
        if (!Guid.TryParse(req.Id, out var id))
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"invalid id: {req.Id}"));

        var entity = await _db.Tasks.FindAsync(new object?[] { id }, context.CancellationToken).ConfigureAwait(false);
        if (entity is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"task {req.Id} not found"));

        entity.Status = TaskMapper.StatusToString(req.Status);
        entity.StatusAt = DateTime.UtcNow;
        if (req.HasExportTarget) entity.ExportTarget = req.ExportTarget;
        if (req.Status == ProtoTaskStatus.Exported) entity.ExportedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);
        _log.LogInformation("Task {Id} status → {Status}", entity.Id, entity.Status);

        var proto = TaskMapper.ToProto(entity);
        Publish(TaskEventType.Updated, proto);
        return proto;
    }

    public override async Task<DeleteTaskResponse> DeleteTask(DeleteTaskRequest req, ServerCallContext context)
    {
        if (!Guid.TryParse(req.Id, out var id))
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"invalid id: {req.Id}"));

        var entity = await _db.Tasks.FindAsync(new object?[] { id }, context.CancellationToken).ConfigureAwait(false);
        if (entity is null) return new DeleteTaskResponse { Deleted = false };

        var snapshot = TaskMapper.ToProto(entity);
        _db.Tasks.Remove(entity);
        await _db.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);
        _log.LogInformation("Task {Id} deleted", entity.Id);

        Publish(TaskEventType.Deleted, snapshot);
        return new DeleteTaskResponse { Deleted = true };
    }

    public override async Task StreamTaskEvents(
        StreamTaskEventsRequest req,
        IServerStreamWriter<TaskEvent> responseStream,
        ServerCallContext context)
    {
        string? userFilter = req.HasUserId ? req.UserId : null;
        _log.LogInformation("StreamTaskEvents subscriber connected (filter: {Filter})", userFilter ?? "*");

        try
        {
            await foreach (var evt in _bus.Subscribe(context.CancellationToken).ConfigureAwait(false))
            {
                if (userFilter is not null && evt.Task.UserId != userFilter) continue;
                await responseStream.WriteAsync(evt).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected. Normal.
        }
        finally
        {
            _log.LogInformation("StreamTaskEvents subscriber disconnected");
        }
    }

    private void Publish(TaskEventType type, TaskItem task)
    {
        _bus.Publish(new TaskEvent
        {
            Type = type,
            Task = task,
            At = Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }
}
