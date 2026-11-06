using System.Net;
using System.Security.Cryptography.X509Certificates;
using SoggyPigeon.Protocol;
using SoggyPigeon.Server;

namespace SoggyPigeon.Server;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        int tcpPort = Proto.DefaultTcpPort, udpPort = Proto.DefaultUdpPort;
        string? advertisedHost = null;
        string? dataDir = null, manifestPath = null;
        ChaosConfig? chaos = null;
        var tls = args.Contains("--tls");

        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--port": int.TryParse(args[i + 1], out tcpPort); break;
                case "--udp": int.TryParse(args[i + 1], out udpPort); break;
                case "--data": dataDir = args[i + 1]; break;
                case "--manifest": manifestPath = args[i + 1]; break;
                case "--public-ip": advertisedHost = args[i + 1]; break;
                case "--chaos":
                    // "drop=20,jitter=60" — fault-inject relayed game traffic
                    double drop = 0; int jitter = 0;
                    foreach (var kv in args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var p = kv.Split('=');
                        if (p.Length == 2 && p[0] == "drop") double.TryParse(p[1], System.Globalization.CultureInfo.InvariantCulture, out drop);
                        if (p.Length == 2 && p[0] == "jitter") int.TryParse(p[1], out jitter);
                    }
                    chaos = new ChaosConfig { DropPercent = drop, JitterMs = jitter };
                    break;
            }
        }

        var manifest = manifestPath is null ? ManifestStore.Default() : ManifestStore.LoadFile(manifestPath);
        dataDir ??= Path.Combine(AppContext.BaseDirectory, "data");

        // log to a file so a hidden-window deployment still leaves a trace.
        // FileShare.Read: a second instance starting up must not die on the
        // open (it crashed the process via an unhandled IOException, live
        // 2026-11-05) — and if the file is still unwritable, run on with
        // console-only logging rather than refusing to serve.
        if (!args.Contains("--no-logfile"))
        {
            Directory.CreateDirectory(dataDir);
            var logPath = Path.Combine(dataDir, "server.log");
            try
            {
                var logFile = new FileStream(logPath, FileMode.Append, FileAccess.Write,
                    FileShare.Read | FileShare.Delete);
                Console.SetOut(TextWriter.Synchronized(new StreamWriter(logFile) { AutoFlush = true }));
            }
            catch (IOException)
            {
                Console.WriteLine($"  warning: {logPath} is locked by another process — logging to console only");
            }
        }

        X509Certificate2? tlsCert = null;
        if (tls)
        {
            tlsCert = TlsSupport.EnsureCertificate(dataDir);
            var pin = TlsPin.Sha256Hex(tlsCert);
            Console.WriteLine("  tls      : ENABLED — clients pin this SHA-256:");
            Console.WriteLine($"             {pin}");
            Console.WriteLine($"             (also written to {TlsSupport.PinPath(dataDir)})");
        }


        await using var server = PigeonServer.Start(tcpPort, udpPort, dataDir, manifest, chaos, tlsCert);
        advertisedHost ??= DetectLanIp() ?? Dns.GetHostName();
        server.AdvertiseRelayHost(advertisedHost);

        Console.WriteLine($"Soggy Pigeon server ({Proto.Name})");
        Console.WriteLine($"  control : tcp *:{server.TcpPort}");
        Console.WriteLine($"  rendezvous/relay: udp *:{server.UdpPort} (advertised as {advertisedHost})");
        Console.WriteLine($"  manifest: {manifest.Current.Games.Length} games, rev {manifest.Current.Rev}");
        Console.WriteLine($"  data dir: {dataDir}");
        Console.WriteLine("Ctrl+C to stop.");

        var done = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
        await done.Task.ConfigureAwait(false);
        Console.WriteLine("Stopping…");
        await server.StopAsync().ConfigureAwait(false);
        return 0;
    }


    /// <summary>The LAN IPv4 a client on the same network would reach us on
    /// (probe-routing trick: nothing is actually sent).</summary>
    private static string? DetectLanIp()
    {
        try
        {
            using var probe = new System.Net.Sockets.UdpClient();
            probe.Connect("192.168.255.255", 9);
            return ((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Address.ToString();
        }
        catch { return null; }
    }
}
