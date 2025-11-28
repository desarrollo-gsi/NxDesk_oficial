using NxDesk.Application.Interfaces;
using NxDesk.Infrastructure.Services;

var builder = Host.CreateApplicationBuilder(args);

// --- 1. Registrar Servicios de Infraestructura ---
builder.Services.AddSingleton<ISignalingService, SignalRSignalingService>();
builder.Services.AddSingleton<IIdentityService, IdentityService>();
builder.Services.AddSingleton<INetworkDiscoveryService, NetworkDiscoveryService>();

// Servicios específicos del Host (Input y WebRTC)
builder.Services.AddSingleton<IInputSimulator, NativeInputSimulator>();
builder.Services.AddSingleton<HostWebRTCService>();

// --- 2. Registrar el Worker Principal ---
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();