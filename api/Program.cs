using Microsoft.EntityFrameworkCore;
using Xerp.Api.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDb>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<AppDb>().Database.MigrateAsync();

app.MapOpenApi();
app.MapControllers();

app.MapGet("/health", async (AppDb db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ok", db = "ok" })
        : Results.Json(new { status = "degraded", db = "down" }, statusCode: 503));

app.Run();
