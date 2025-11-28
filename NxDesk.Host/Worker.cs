using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NxDesk.Application.Interfaces;
using NxDesk.Infrastructure.Services;

public class Worker : BackgroundService
{
    private readonly HostWebRTCService _hostService;
    private readonly IIdentityService _identityService;
    private readonly INetworkDiscoveryService _discoveryService;
    private readonly ILogger<Worker> _logger;

    public Worker(HostWebRTCService hostService, IIdentityService identityService, INetworkDiscoveryService discoveryService, ILogger<Worker> logger)
    {
        _hostService = hostService;
        _identityService = identityService;
        _discoveryService = discoveryService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var myId = _identityService.GetMyId();
        _logger.LogInformation($"[NxDesk Host] Iniciando. ID: {myId}");

        // Iniciar Discovery para que nos vean en la red local
        _discoveryService.Start();

        // Iniciar conexión al servidor de señalización y esperar llamadas
        await _hostService.StartAsync(myId);

        // Mantener el servicio vivo
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
    }
}