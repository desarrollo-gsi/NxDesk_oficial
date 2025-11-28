using NxDesk.Application.Interfaces;
using NxDesk.Infrastructure.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<ISignalingService, SignalRSignalingService>();
builder.Services.AddSingleton<IIdentityService, IdentityService>();
builder.Services.AddSingleton<INetworkDiscoveryService, NetworkDiscoveryService>();

builder.Services.AddSingleton<IInputSimulator, NativeInputSimulator>();
builder.Services.AddSingleton<HostWebRTCService>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();