using Grpc.Core;
using WorkIntel.Contracts.V1;
using Xunit;
using ProtoTaskStatus = WorkIntel.Contracts.V1.TaskStatus;

namespace WorkIntel.Api.Tests;

[Collection(nameof(ApiCollection))]
public sealed class TaskServiceTests
{
    private readonly TaskService.TaskServiceClient _client;

    public TaskServiceTests(ApiFixture fixture)
    {
        _client = new TaskService.TaskServiceClient(fixture.Channel);
    }

    [Fact]
    public async Task CreateAndGet_RoundTrip()
    {
        var created = await _client.CreateTaskAsync(new CreateTaskRequest
        {
            Task = NewTask("roundtrip — fix the export bug"),
        });

        Assert.False(string.IsNullOrEmpty(created.Id));
        Assert.Equal("roundtrip — fix the export bug", created.Title);
        Assert.Equal(ProtoTaskStatus.Pending, created.Status);

        var fetched = await _client.GetTaskAsync(new GetTaskRequest { Id = created.Id });
        Assert.Equal(created.Id, fetched.Id);
        Assert.Equal(created.Title, fetched.Title);
    }

    [Fact]
    public async Task Create_WithoutTitle_ReturnsInvalidArgument()
    {
        var task = NewTask("");
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _client.CreateTaskAsync(new CreateTaskRequest { Task = task }).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task Get_WithBadId_ReturnsInvalidArgument()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _client.GetTaskAsync(new GetTaskRequest { Id = "not-a-guid" }).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task Get_NotFound_ReturnsNotFound()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _client.GetTaskAsync(new GetTaskRequest { Id = Guid.NewGuid().ToString() }).ResponseAsync);

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task ListTasks_OrdersByDetectedAtDescending()
    {
        await _client.CreateTaskAsync(new CreateTaskRequest { Task = NewTask("ordering — first") });
        await Task.Delay(50);
        await _client.CreateTaskAsync(new CreateTaskRequest { Task = NewTask("ordering — second") });

        var resp = await _client.ListTasksAsync(new ListTasksRequest { UserId = "local", PageSize = 10 });

        // The two we just created should be near the top; "second" before "first".
        var titles = resp.Tasks.Select(t => t.Title).ToList();
        var firstIdx = titles.IndexOf("ordering — first");
        var secondIdx = titles.IndexOf("ordering — second");
        Assert.True(secondIdx >= 0 && firstIdx >= 0, "both created tasks should be returned");
        Assert.True(secondIdx < firstIdx, "newer task should appear before older");
    }

    [Fact]
    public async Task ListTasks_FiltersByStatus()
    {
        var keep = await _client.CreateTaskAsync(new CreateTaskRequest { Task = NewTask("status filter — keep") });
        await _client.UpdateTaskStatusAsync(new UpdateTaskStatusRequest
        {
            Id = keep.Id,
            Status = ProtoTaskStatus.Included,
        });

        await _client.CreateTaskAsync(new CreateTaskRequest { Task = NewTask("status filter — pending") });

        var included = await _client.ListTasksAsync(new ListTasksRequest
        {
            UserId = "local",
            Status = ProtoTaskStatus.Included,
            PageSize = 100,
        });

        Assert.Contains(included.Tasks, t => t.Id == keep.Id);
        Assert.All(included.Tasks, t => Assert.Equal(ProtoTaskStatus.Included, t.Status));
    }

    [Fact]
    public async Task UpdateTaskStatus_RecordsExportedAt_WhenStatusIsExported()
    {
        var created = await _client.CreateTaskAsync(new CreateTaskRequest { Task = NewTask("export-stamp") });

        var updated = await _client.UpdateTaskStatusAsync(new UpdateTaskStatusRequest
        {
            Id = created.Id,
            Status = ProtoTaskStatus.Exported,
            ExportTarget = "trello-personal",
        });

        Assert.Equal(ProtoTaskStatus.Exported, updated.Status);
        Assert.Equal("trello-personal", updated.ExportTarget);
        Assert.NotNull(updated.ExportedAt);
    }

    [Fact]
    public async Task DeleteTask_Twice_FirstReturnsTrue_SecondReturnsFalse()
    {
        var created = await _client.CreateTaskAsync(new CreateTaskRequest { Task = NewTask("delete-twice") });

        var first = await _client.DeleteTaskAsync(new DeleteTaskRequest { Id = created.Id });
        var second = await _client.DeleteTaskAsync(new DeleteTaskRequest { Id = created.Id });

        Assert.True(first.Deleted);
        Assert.False(second.Deleted);
    }

    private static TaskItem NewTask(string title) => new()
    {
        UserId = "local",
        Source = TaskSource.Audio,
        SourceMetaJson = """{"segment_id":"test"}""",
        OriginalText = "test transcript",
        Title = title,
        Confidence = 0.5,
        Status = ProtoTaskStatus.Pending,
    };
}
