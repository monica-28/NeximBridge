using System.Net.Sockets;

namespace NeximBridge;

public sealed class NeximTcpClientService(IConfiguration configuration, ILogger<NeximTcpClientService> logger)
    : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectionCheckInterval = TimeSpan.FromSeconds(1);
    private readonly object syncRoot = new();
    private TcpClient? tcpClient;
    private NetworkStream? networkStream;

    public bool IsConnected
    {
        get
        {
            lock (syncRoot)
            {
                return tcpClient?.Connected == true && networkStream is not null;
            }
        }
    }

    public NetworkStream? NetworkStream
    {
        get
        {
            lock (syncRoot)
            {
                return networkStream;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAsync(stoppingToken);
                await MonitorConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Error en la conexión TCP con Nexim.");
            }
            finally
            {
                Disconnect();
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Reintentando la conexión con Nexim en {Delay}.", ReconnectDelay);
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            return;
        }

        string host = configuration["Nexim:Host"]
            ?? throw new InvalidOperationException("Falta la configuración Nexim:Host.");
        int port = configuration.GetValue<int?>("Nexim:Port")
            ?? throw new InvalidOperationException("Falta la configuración Nexim:Port.");

        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException("La configuración Nexim:Port debe estar entre 1 y 65535.");
        }

        TcpClient client = new();

        try
        {
            await client.ConnectAsync(host, port, cancellationToken);
            NetworkStream stream = client.GetStream();

            lock (syncRoot)
            {
                tcpClient = client;
                networkStream = stream;
            }

            logger.LogInformation("Conectado a Nexim en {Host}:{Port}.", host, port);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public void Disconnect(NetworkStream? expectedStream = null)
    {
        TcpClient? client;

        lock (syncRoot)
        {
            if (expectedStream is not null && !ReferenceEquals(networkStream, expectedStream)) return;
            client = tcpClient;
            tcpClient = null;
            networkStream = null;
        }

        if (client is not null)
        {
            client.Dispose();
            logger.LogInformation("Desconectado de Nexim.");
        }
    }

    public override void Dispose()
    {
        Disconnect();
        base.Dispose();
    }

    private async Task MonitorConnectionAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client;

            lock (syncRoot)
            {
                client = tcpClient;
            }

            if (client is null || !client.Connected)
            {
                throw new IOException("La conexión TCP con Nexim se ha cerrado.");
            }

            if (client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0)
            {
                throw new IOException("Nexim cerró la conexión TCP.");
            }

            await Task.Delay(ConnectionCheckInterval, cancellationToken);
        }
    }
}
