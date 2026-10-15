using WANT.Service.Dhcp;
using WANT.Service.Packets;
using WANT.Service.Rip;
using WANT.Service.Routing;
using WANT.Service;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
    options.SwaggerDoc("v1", new() { Title = "WANT API", Version = "v1" }));
builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new WANT.Controller.Dto.IPAddressJsonConverter()));

builder.Services.AddSingleton<InterfaceScanner>();
builder.Services.AddSingleton<ArpCache>();
builder.Services.AddSingleton<IRoutingTable, RoutingTable>();
builder.Services.AddSingleton<IRoutingService, RoutingService>();
builder.Services.AddSingleton<WANT.Service.Packets.IArpService, WANT.Service.Packets.ArpService>();

builder.Services.AddHostedService<PortScannerWorker>();
// Controllers and hosted workers must share the same service instances.
builder.Services.AddSingleton<PacketBridgeWorker>();
builder.Services.AddHostedService(services => services.GetRequiredService<PacketBridgeWorker>());
builder.Services.AddSingleton<IRipService, RipService>();
builder.Services.AddHostedService(services => (RipService)services.GetRequiredService<IRipService>());
builder.Services.AddSingleton<IDhcpService, DhcpService>();
builder.Services.AddHostedService(services => (DhcpService)services.GetRequiredService<IDhcpService>());

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
// Wire the packet sender after construction to avoid a circular DI dependency.
var packetBridge = app.Services.GetRequiredService<PacketBridgeWorker>();
(app.Services.GetRequiredService<IRipService>() as RipService)?.SetPacketBridge(packetBridge);

app.UseSwagger();
app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "WANT API v1"));
app.UseCors();
app.MapControllers();
app.Run();
