using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using SmtpServer;
using SmtpServer.Storage;
using SrsProxy;

var builder = Host.CreateApplicationBuilder(args);

if (OperatingSystem.IsWindows())
{
    builder.Services.AddWindowsService(options => options.ServiceName = "SrsProxy");
#pragma warning disable CA1416
    builder.Logging.AddEventLog(settings => settings.SourceName = "SrsProxy");
#pragma warning restore CA1416
}
else
{
    builder.Services.AddSystemd();
}

var listenPort = builder.Configuration.GetValue("SrsProxy:ListenPort", 2525);
var localOnly = builder.Configuration.GetValue("SrsProxy:LocalOnly", true);
var listenAddress = localOnly ? IPAddress.Loopback : IPAddress.Any;

builder.Services.AddTransient<IMessageStore, SrsMessageStore>();

builder.Services.AddSingleton(provider =>
{
    var options = new SmtpServerOptionsBuilder()
        .ServerName("SrsProxy")
        .Endpoint(e => e
            .Endpoint(new IPEndPoint(listenAddress, listenPort))
            .AllowUnsecureAuthentication(true))
        .Build();

    return new SmtpServer.SmtpServer(options, provider);
});

builder.Services.AddHostedService<SmtpWorker>();

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("SrsProxy starting on {Address}:{Port}", listenAddress, listenPort);

host.Run();

sealed class SmtpWorker(SmtpServer.SmtpServer smtpServer, ILogger<SmtpWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SMTP relay server starting...");

        try
        {
            await smtpServer.StartAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("SMTP relay server stopped.");
        }
    }
}
