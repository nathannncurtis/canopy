using SizeMonitor.LocalApi;

int port = 0;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[++i], out int parsed) && parsed is > 0 and <= 65535)
        port = parsed;
    else
    {
        Console.Error.WriteLine("Usage: canopy-local-api [--port 1-65535]");
        return 2;
    }
}

await using var host = new LocalScanApiHost(port);
await host.StartAsync();
Console.WriteLine($"Canopy local API: {host.Address}");
Console.WriteLine($"Bearer token: {host.BearerToken}");
Console.WriteLine("The API is bound to this machine only. Press Ctrl+C to stop.");
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
try { await Task.Delay(Timeout.Infinite, stop.Token); }
catch (OperationCanceledException) { }
return 0;
