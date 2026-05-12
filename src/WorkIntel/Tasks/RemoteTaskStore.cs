using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using WorkIntel.App;
using WorkIntel.Contracts.V1;

namespace WorkIntel.Tasks;

/// <summary>
/// Thin desktop-side wrapper around the gRPC <see cref="TaskService"/>. Owns
/// the channel lifetime, exposes a clean async surface for create/list/update/
/// delete, and runs a background subscription to <c>StreamTaskEvents</c> so the
/// UI can update reactively on any change (regardless of which client made it).
/// </summary>
/// <remarks>
/// Phase 1: no auth, cleartext HTTP/2 on localhost. The
/// <c>SocketsHttpHandler.Http2UnencryptedSupport</c> AppContext switch must be
/// flipped on before any channel is constructed (done in <c>Program.cs</c>).
/// </remarks>
public sealed class RemoteTaskStore : IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly TaskService.TaskServiceClient _client;
    private readonly CancellationTokenSource _streamCts = new();
    private Task? _streamTask;

    public string Endpoint { get; }

    /// <summary>Raised whenever the API publishes a task event (created/updated/deleted).
    /// Fires on a background thread — UI subscribers must marshal to their thread.</summary>
    public event EventHandler<TaskEvent>? TaskChanged;

    /// <summary>Raised when the stream connection state changes (connected / disconnected / failed).
    /// Useful for the UI status indicator.</summary>
    public event EventHandler<StreamState>? StreamStateChanged;

    public RemoteTaskStore(string endpoint)
    {
        Endpoint = endpoint;
        _channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions
        {
            // Reasonable retry shape for a long-lived desktop session.
            MaxReceiveMessageSize = 4 * 1024 * 1024,
        });
        _client = new TaskService.TaskServiceClient(_channel);
    }

    /// <summary>Kick off the background subscription. Idempotent.</summary>
    public void StartEventStream()
    {
        if (_streamTask is not null) return;
        _streamTask = Task.Run(() => StreamLoop(_streamCts.Token));
    }

    public async Task<TaskItem> CreateAsync(
        TaskCandidate candidate,
        TaskSource source,
        IReadOnlyDictionary<string, string?>? sourceMeta,
        string originalText,
        CancellationToken ct = default)
    {
        var task = new TaskItem
        {
            UserId = "local",
            Source = source,
            SourceMetaJson = sourceMeta is null ? "{}" : JsonSerializer.Serialize(sourceMeta),
            OriginalText = originalText,
            Title = candidate.Title,
            Confidence = candidate.Confidence,
            Status = WorkIntel.Contracts.V1.TaskStatus.Pending,
        };
        if (!string.IsNullOrWhiteSpace(candidate.Description)) task.Description = candidate.Description;
        if (!string.IsNullOrWhiteSpace(candidate.Owner))       task.Owner = candidate.Owner;
        if (!string.IsNullOrWhiteSpace(candidate.Deadline))    task.Deadline = candidate.Deadline;

        return await _client.CreateTaskAsync(new CreateTaskRequest { Task = task }, cancellationToken: ct);
    }

    public async Task<IReadOnlyList<TaskItem>> ListAsync(
        WorkIntel.Contracts.V1.TaskStatus? filter = null,
        int pageSize = 100,
        CancellationToken ct = default)
    {
        var req = new ListTasksRequest
        {
            UserId = "local",
            PageSize = pageSize,
        };
        if (filter is not null && filter != WorkIntel.Contracts.V1.TaskStatus.Unspecified)
            req.Status = filter.Value;

        var resp = await _client.ListTasksAsync(req, cancellationToken: ct);
        return resp.Tasks;
    }

    public async Task<TaskItem> UpdateStatusAsync(
        string id,
        WorkIntel.Contracts.V1.TaskStatus status,
        string? exportTarget = null,
        CancellationToken ct = default)
    {
        var req = new UpdateTaskStatusRequest { Id = id, Status = status };
        if (exportTarget is not null) req.ExportTarget = exportTarget;
        return await _client.UpdateTaskStatusAsync(req, cancellationToken: ct);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        var resp = await _client.DeleteTaskAsync(new DeleteTaskRequest { Id = id }, cancellationToken: ct);
        return resp.Deleted;
    }

    /// <summary>One-shot health probe; used by the Settings "Test connection" path.</summary>
    public async Task<string> PingAsync(CancellationToken ct = default)
    {
        await _client.ListTasksAsync(new ListTasksRequest { PageSize = 1 }, cancellationToken: ct);
        return "ok";
    }

    private async Task StreamLoop(CancellationToken ct)
    {
        // Reconnect-with-backoff loop. gRPC streams die under various conditions
        // (server restart, transient network blip); we just reopen.
        var backoff = TimeSpan.FromSeconds(1);
        var maxBackoff = TimeSpan.FromSeconds(30);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                StreamStateChanged?.Invoke(this, StreamState.Connecting);
                using var call = _client.StreamTaskEvents(
                    new StreamTaskEventsRequest { UserId = "local" },
                    cancellationToken: ct);

                StreamStateChanged?.Invoke(this, StreamState.Connected);
                backoff = TimeSpan.FromSeconds(1); // reset after a clean connect

                await foreach (var evt in call.ResponseStream.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    try { TaskChanged?.Invoke(this, evt); }
                    catch (Exception ex) { Log.Warn($"TaskChanged subscriber threw: {ex.Message}"); }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (RpcException rex)
            {
                Log.Warn($"task event stream error: {rex.StatusCode} {rex.Status.Detail}");
                StreamStateChanged?.Invoke(this, StreamState.Disconnected);
            }
            catch (Exception ex)
            {
                Log.Warn($"task event stream threw: {ex.Message}");
                StreamStateChanged?.Invoke(this, StreamState.Disconnected);
            }

            try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            backoff = TimeSpan.FromTicks(Math.Min((backoff * 2).Ticks, maxBackoff.Ticks));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _streamCts.Cancel();
        if (_streamTask is not null)
        {
            try { await _streamTask.ConfigureAwait(false); } catch { /* swallow */ }
        }
        _streamCts.Dispose();
        _channel.Dispose();
    }
}

public enum StreamState { Connecting, Connected, Disconnected }
