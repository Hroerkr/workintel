using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Serilog;
using WorkIntel.Api.Services;
using WorkIntel.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, services, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

// Two listeners. Over cleartext there's no ALPN to negotiate per-connection,
// so we can't serve both gRPC and REST on the same port reliably — Kestrel
// responds to gRPC's HTTP/2 prior-knowledge with HTTP_1_1_REQUIRED when both
// protocols share the port without TLS. Once TLS is in front, ALPN negotiates
// and a single port works fine.
//
//   :5000  HTTP/2 cleartext, gRPC only
//   :5001  HTTP/1.1, REST endpoints (/, /health, future curl/browser debug)
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5000, listen => listen.Protocols = HttpProtocols.Http2);
    options.ListenAnyIP(5001, listen => listen.Protocols = HttpProtocols.Http1);
});

var connStr = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");

builder.Services.AddDbContext<WorkIntelDbContext>(opts =>
{
    opts.UseNpgsql(connStr);
    if (builder.Environment.IsDevelopment())
        opts.EnableSensitiveDataLogging();
});

builder.Services.AddGrpc(o =>
{
    o.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

builder.Services.AddSingleton<TaskEventBus>();
builder.Services.AddScoped<TaskService>();

var app = builder.Build();

// Apply the schema on startup. EnsureCreated for the POC; switch to migrations
// (`dotnet ef migrations add …`) once the schema stops moving.
using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<WorkIntelDbContext>();
    await ctx.Database.EnsureCreatedAsync();
    Log.Information("Database schema ensured");
}

app.MapGrpcService<TaskService>();

// Liveness probe + a friendly root response so curl localhost:5000/ doesn't
// give a confusing "this is a gRPC endpoint" error.
app.MapGet("/", () => Results.Text(
    "WorkIntel API. gRPC service at this host:port. Health at /health.",
    "text/plain"));
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

await app.RunAsync();

// Expose Program so WebApplicationFactory<Program> can find it in tests.
public partial class Program;
