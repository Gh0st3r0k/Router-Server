using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new() { Title = "WANT API", Version = "v1" });
});

var app = builder.Build();

// Swagger UI
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "WANT API v1");
    // Чтобы UI открывался прямо на корне:
    // c.RoutePrefix = string.Empty;
});

// Проверка Ethernet портов при старте
NicDebugger.PrintPhysicalEthernet();

// тестовый эндпоинт
app.MapGet("/api/ping", () =>
    Results.Ok(new { status = "ok", time = DateTime.UtcNow }))
   .WithName("Ping");

app.Run();
