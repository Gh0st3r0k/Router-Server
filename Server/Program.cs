using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Server.Services.Packets;
using WANT.Service;               // для InterfaceScanner
using WANT.Service.Interface;     // для InterfaceSnapshotUpdater и моделей
using WANT.Service.Packets;
using WANT.Service.Routing;


var builder = WebApplication.CreateBuilder(args);

// ── Swagger ─────────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new() { Title = "WANT API", Version = "v1" });
});

// !!! Контроллеры
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new WANT.Controller.Dto.IPAddressJsonConverter());
    });

// ── Регистрация фоновых служб (когда появятся классы) ──────────────────────────
builder.Services.AddHostedService<PortScannerWorker>();   // фоновые проверки каждые 5 сек
builder.Services.AddSingleton<ArpCache>();             // ⬅️ кэш

// Хранилище интерфейсов
builder.Services.AddSingleton<InterfaceScanner>();

// Таблица маршрутизации
builder.Services.AddSingleton<IRoutingTable, RoutingTable>();

// Сервис маршрутизации (с “реверсным” next-hop)
builder.Services.AddSingleton<IRoutingService, RoutingService>();

// Сам воркер как singleton, чтобы можно было инжектить по типу PacketBridgeWorker
builder.Services.AddSingleton<PacketBridgeWorker>();

// И тот же инстанс запустить как фоновой сервис
builder.Services.AddHostedService(sp => sp.GetRequiredService<PacketBridgeWorker>());

// ARP-сервис
builder.Services.AddSingleton<
    WANT.Service.Packets.IArpService,
    WANT.Service.Packets.ArpService
>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();


// ── Swagger UI ──────────────────────────────────────────────────────────────────
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "WANT API v1");
    // Чтобы Swagger UI открывался на корне — раскомментируй:
    // c.RoutePrefix = string.Empty;
});

// ── Хуки жизненного цикла ───────────────────────────────────────────────────────
app.Lifetime.ApplicationStarted.Register(() =>
{
    // Тут вызывать параллельные процессы (разовые/краткоживущие), например:
    // Task.Run(() => ParallelJobs.SyncRouterPortsAsync(app.Services, CancellationToken.None));
    // Task.Run(() => ParallelJobs.WarmupAsync(app.Services, CancellationToken.None));

    // Проверка Ethernet портов при старте (если нужно выполнить один раз):
    // NicDebugger.PrintPhysicalEthernet();
});

// var hosted = app.Services.GetServices<IHostedService>();
// Console.WriteLine("[Program] Registered IHostedService:");
// foreach (var hs in hosted)
// {
//     Console.WriteLine(" - " + hs.GetType().FullName);
// }

// Console.WriteLine("[Program] app built, starting web host…");

// !!! Маршрутизация контроллеров

app.UseCors();

app.MapControllers();

app.Run();
