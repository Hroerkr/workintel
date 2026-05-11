using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;
using Xunit;

namespace WorkIntel.Api.Tests;

/// <summary>
/// Shared fixture: spins up one Postgres container + one in-memory API host
/// per test collection. Tests share the same DB across runs in a collection;
/// each test that cares about isolation should use distinct ids / titles.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private PostgreSqlContainer _postgres = null!;
    private WebApplicationFactory<Program> _factory = null!;

    public GrpcChannel Channel { get; private set; } = null!;
    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("workintel_test")
            .WithUsername("workintel")
            .WithPassword("workintel")
            .Build();
        await _postgres.StartAsync();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Default"] = _postgres.GetConnectionString(),
                    });
                });
            });

        // Create an HTTP/2-capable in-memory client routed at the test host.
        var handler = _factory.Server.CreateHandler();
        var http = new HttpClient(handler) { BaseAddress = _factory.Server.BaseAddress };
        Channel = GrpcChannel.ForAddress(_factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpClient = http,
        });
    }

    public async Task DisposeAsync()
    {
        Channel.Dispose();
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

[CollectionDefinition(nameof(ApiCollection))]
public sealed class ApiCollection : ICollectionFixture<ApiFixture> { }
