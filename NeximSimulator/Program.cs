using System.Net;
using System.Net.Sockets;
using NeximBridge;
using NeximSimulator;

using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
using TcpListener listener = new(IPAddress.Loopback, 30040);
listener.Start();
Console.WriteLine("NeximSimulator escuchando en 127.0.0.1:30040...");
try
{
    while (!cancellation.IsCancellationRequested)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(cancellation.Token);
        await using NetworkStream stream = client.GetStream();
        try
        {
            SimulatorInitialization initialization = new();
            await initialization.RunAsync(stream, TimeSpan.FromSeconds(10), cancellation.Token);
            Console.WriteLine("SETEV y STARTEV completados. Estado: recepción.");
            if (!initialization.Events.TryGetValue("PARTSUSAGE", out bool requiresAck) || !requiresAck)
                throw new IOException("La prueba requiere una suscripción SETEV a PARTSUSAGE con ACK=1.");
            if (!initialization.Events.TryGetValue("PDERROR", out bool pdErrorRequiresAck) || !pdErrorRequiresAck)
                throw new IOException("La prueba requiere una suscripción SETEV a PDERROR con ACK=1.");
            await PartsUsageScenario.RunAsync(stream, cancellation.Token);
            Console.WriteLine("PARTSUSAGE_ACK recibido y validado: SeqID=1001.");
            await PdErrorScenario.RunAsync(stream, cancellation.Token);
            if (!initialization.Events.TryGetValue("PRODSTARTED", out bool prodStartedRequiresAck) || !prodStartedRequiresAck)
                throw new IOException("La prueba requiere una suscripción SETEV a PRODSTARTED con ACK=1.");
            await ProdStartedScenario.RunAsync(stream, cancellation.Token);
            if (!initialization.Events.TryGetValue("PRODCOMPLETEDII", out bool prodCompletedIIRequiresAck) || !prodCompletedIIRequiresAck)
                throw new IOException("La prueba requiere una suscripción SETEV a PRODCOMPLETEDII con ACK=1.");
            await ProdCompletedIIScenario.RunAsync(stream, cancellation.Token);
            if (!initialization.Events.TryGetValue("PRODSTOPPED", out bool prodStoppedRequiresAck) || !prodStoppedRequiresAck)
                throw new IOException("La prueba requiere una suscripción SETEV a PRODSTOPPED con ACK=1.");
            await ProdStoppedScenario.RunAsync(stream, cancellation.Token);
            if (!initialization.Events.TryGetValue("LOADCOMPIV", out bool loadCompIVAck) || loadCompIVAck)
                throw new IOException("La prueba requiere SETEV LOADCOMPIV con ACK=0.");
            await LoadCompIVScenario.RunAsync(stream, cancellation.Token);
            if (!initialization.Events.TryGetValue("CHANGECOMPII", out bool changeCompIIAck) || changeCompIIAck)
                throw new IOException("La prueba requiere SETEV CHANGECOMPII con ACK=0.");
            await ChangeCompIIScenario.RunAsync(stream, cancellation.Token);
            if (!initialization.Events.TryGetValue("UNLOADCOMP", out bool unloadCompAck) || unloadCompAck)
                throw new IOException("La prueba requiere SETEV UNLOADCOMP con ACK=0.");
            await UnloadCompScenario.RunAsync(stream, cancellation.Token);
            if (!initialization.Events.TryGetValue("MCALARMON", out bool mcAlarmOnAck) || !mcAlarmOnAck)
                throw new IOException("La prueba requiere SETEV MCALARMON con ACK=1.");
            await McAlarmOnScenario.RunAsync(stream, cancellation.Token);
            if (!initialization.Events.TryGetValue("MCALARMOFF", out bool mcAlarmOffAck) || !mcAlarmOffAck)
                throw new IOException("La prueba requiere SETEV MCALARMOFF con ACK=1.");
            await McAlarmOffScenario.RunAsync(stream, cancellation.Token);
            Console.WriteLine("Conexi\u00f3n mantenida. Estado: recepci\u00f3n; KEEPALIVE cada 120 segundos.");
            await KeepAliveSession.RunAsync(stream, cancellation.Token);
        }
        catch (Exception exception) when (!cancellation.IsCancellationRequested)
        {
            Console.WriteLine($"Sesión cerrada: {exception.Message}");
        }
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
