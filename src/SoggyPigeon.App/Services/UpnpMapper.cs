using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace SoggyPigeon.App.Services;

/// <summary>The result of one UPnP attempt: mapped says whether the router
/// now forwards the port; when it does not, Error says why (also logged) and
/// ExternalIp may still carry what the router reported as its outermost
/// address (the CGNAT / second-router evidence for the punch verdict).</summary>
public sealed record UpnpResult(
    bool Mapped,
    int Port,
    string LocalIp,
    string ExternalIp,
    string Control,
    string Service,
    string Error);

/// <summary>
/// UPnP port mapping on the router (the session's UDP port), ported from the
/// Tekken6 Native Online launcher's upnp.cpp (2026-10-17): SSDP-discover the
/// gateway, fetch its description, SOAP AddPortMapping with external =
/// internal so a strict NAT cannot rewrite the punch port. Best-effort: any
/// failure leaves the punch running unchanged, and the punch verdict reads
/// what UPnP did or did not learn.
/// </summary>
public static partial class UpnpMapper
{
    private static readonly string[] SearchTargets =
    [
        "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
        "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
    ];

    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false });

    /// <summary>The router found last time (control URL, service, external
    /// address): later matches then cost one SOAP call instead of a fresh
    /// SSDP sweep. Cleared when a mapping through it fails.</summary>
    private static (string Control, string Service, string ExternalIp, string Host, int Port)? _cached;

    /// <summary>The T6NO privateIpv4 guard: RFC1918, CGNAT 100.64/10 and the
    /// unspecified address. True also for anything unparseable (treat
    /// unknown as private: the verdict then never claims a public address).</summary>
    public static bool IsPrivateIpv4(string? ip)
    {
        if (ip is null || !IPAddress.TryParse(ip, out var addr)
            || addr.AddressFamily != AddressFamily.InterNetwork)
            return true;
        var b = addr.GetAddressBytes();
        uint v = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        return v == 0
            || (v >> 24) == 10                       // 10.0.0.0/8
            || (v >> 20) == 0xAC1                    // 172.16.0.0/12
            || (v >> 16) == 0xC0A8                   // 192.168.0.0/16
            || (v >> 22) == (100u << 2 | 1);         // 100.64.0.0/10
    }

    /// <summary>Map `port` on the router (best-effort, seconds). Never
    /// throws; a failed attempt just fills Error.</summary>
    public static Task<UpnpResult> MapAsync(int port) => Task.Run(() => Map(port));

    public static UpnpResult Map(int port)
    {
        UpnpResult r;
        try
        {
            // the cached gateway first: one SOAP call instead of the sweep
            if (_cached is { } c)
            {
                r = AddMapping(c, port);
                if (r.Mapped) return r;
                _cached = null;   // stale (rebooted, changed): rediscover
            }
            var d = Discover();
            if (d is null)
                r = new UpnpResult(false, port, "", "", "", "", "no UPnP router answered");
            else
            {
                r = AddMapping(d.Value, port);
                if (r.Mapped) _cached = d;
            }
        }
        catch (Exception ex)
        {
            r = new UpnpResult(false, port, "", "", "", "", $"UPnP failed: {ex.Message}");
        }
        NetLog.Write($"upnp map {port}: mapped={r.Mapped} local={r.LocalIp} ext={r.ExternalIp} err={r.Error}");
        return r;
    }

    /// <summary>Take the mapping back down (match over: the port rebinds to
    /// nothing and the router keeps its table clean). Fire-and-forget.</summary>
    public static Task UnmapAsync(UpnpResult mapping) => Task.Run(() =>
    {
        try
        {
            _ = Soap(mapping.Control, mapping.Service, "DeletePortMapping",
                $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{mapping.Port}</NewExternalPort>"
                + "<NewProtocol>UDP</NewProtocol>");
            NetLog.Write($"upnp unmap {mapping.Port}: sent");
        }
        catch { /* best effort by contract */ }
    });

    // ---- the pieces, in the order upnp.cpp runs them ----

    /// <summary>SSDP M-SEARCH to the multicast group; collect description
    /// locations that answered from their own private LAN address (only a
    /// description on the answering device itself, in the LAN: no other
    /// address is fetched or sent SOAP to because a device said so — the
    /// T6NO review guard, kept).</summary>
    private static (string Control, string Service, string ExternalIp, string Host, int Port)? Discover()
    {
        using var ssdp = new UdpClient(AddressFamily.InterNetwork);
        ssdp.Client.ReceiveTimeout = 500;
        var group = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        foreach (var st in SearchTargets)
        {
            var m = Encoding.ASCII.GetBytes(
                "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\n"
                + "MAN: \"ssdp:discover\"\r\nMX: 2\r\nST: " + st + "\r\n\r\n");
            _ = ssdp.Send(m, m.Length, group);
        }

        var locations = new List<string>();
        var until = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < until)
        {
            byte[] data;
            var from = new IPEndPoint(IPAddress.Any, 0);
            try { data = ssdp.Receive(ref from); }
            catch (SocketException)
            {
                if (locations.Count > 0) break;   // answers come in one burst
                continue;
            }
            foreach (var line in Encoding.UTF8.GetString(data).Split("\r\n"))
                if (line.StartsWith("location:", StringComparison.OrdinalIgnoreCase))
                {
                    var loc = line["location:".Length..].Trim();
                    if (Uri.TryCreate(loc, UriKind.Absolute, out var lu)
                        && lu.Scheme == "http"
                        && lu.Host == from.Address.ToString()
                        && IsPrivateIpv4(lu.Host)
                        && !locations.Contains(loc))
                        locations.Add(loc);
                }
        }

        foreach (var loc in locations)
        {
            var xml = HttpString(loc);
            if (xml is null) continue;

            // WANIPConnection / WANPPPConnection services of this device
            var services = new List<(string Type, string Ctrl)>();
            foreach (Match svc in ServiceRx().Matches(xml))
            {
                var block = svc.Groups[1].Value;
                var type = XmlValue(block, "serviceType");
                var ctrl = XmlValue(block, "controlURL");
                if (type.Length > 0 && ctrl.Length > 0) services.Add((type, ctrl));
            }
            var urlBase = XmlValue(xml, "URLBase");

            foreach (var (type, ctrl) in services)
            {
                if (!type.Contains("WANIPConnection") && !type.Contains("WANPPPConnection")) continue;
                if (!Uri.TryCreate(loc, UriKind.Absolute, out var locUri)) continue;
                var baseUri = urlBase.Length > 0 && Uri.TryCreate(urlBase, UriKind.Absolute, out var b)
                    ? b : locUri;
                if (!Uri.TryCreate(baseUri, ctrl, out var ctrlUri)) continue;
                // the control point on another host than the device: not followed
                if (ctrlUri.Host != locUri.Host) continue;

                var (st, body) = Soap(ctrlUri.ToString(), type, "GetExternalIPAddress", "");
                var external = st == 200 ? XmlValue(body, "NewExternalIPAddress") : "";
                // a WAN service that is not connected
                if (external.Length == 0 || external == "0.0.0.0") continue;

                var port = locUri.Port == -1 ? 80 : locUri.Port;
                return (ctrlUri.ToString(), type, external, locUri.Host, port);
            }
        }
        return null;
    }

    /// <summary>AddPortMapping external = internal (the punch port survives
    /// the NAT unchanged), seven-day lease — retried as a permanent mapping
    /// when the router only takes those (UPnP error 725).</summary>
    private static UpnpResult AddMapping(
        (string Control, string Service, string ExternalIp, string Host, int Port) d, int port)
    {
        var localIp = LocalIpToward(d.Host, d.Port);
        if (localIp is null)
            return new UpnpResult(false, port, "", d.ExternalIp, d.Control, d.Service,
                "this PC's address towards the router not found");

        var (st, body) = Soap(d.Control, d.Service, "AddPortMapping", AddArgs(port, localIp, 604800));
        if (st != 200 && XmlValue(body, "errorCode") == "725")
            (st, body) = Soap(d.Control, d.Service, "AddPortMapping", AddArgs(port, localIp, 0));
        if (st == 200)
            return new UpnpResult(true, port, localIp, d.ExternalIp, d.Control, d.Service, "");
        return new UpnpResult(false, port, localIp, d.ExternalIp, d.Control, d.Service,
            $"AddPortMapping: HTTP {st}, UPnP error {XmlValue(body, "errorCode")} {XmlValue(body, "errorDescription")}");
    }

    private static string AddArgs(int port, string localIp, int lease) =>
        "<NewRemoteHost></NewRemoteHost>"
        + $"<NewExternalPort>{port}</NewExternalPort>"
        + "<NewProtocol>UDP</NewProtocol>"
        + $"<NewInternalPort>{port}</NewInternalPort>"
        + $"<NewInternalClient>{localIp}</NewInternalClient>"
        + "<NewEnabled>1</NewEnabled>"
        + "<NewPortMappingDescription>Soggy Pigeon</NewPortMappingDescription>"
        + $"<NewLeaseDuration>{lease}</NewLeaseDuration>";

    /// <summary>This PC's address towards the router: a UDP connect uses the
    /// same route the SOAP calls will take (T6NO's getsockname trick).</summary>
    private static string? LocalIpToward(string host, int port)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(host, port);
            return ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
        }
        catch { return null; }
    }

    private static (int Status, string Body) Soap(
        string control, string service, string action, string args)
    {
        var body = "<?xml version=\"1.0\"?>"
            + "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" "
            + "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>"
            + $"<u:{action} xmlns:u=\"{service}\">{args}</u:{action}>"
            + "</s:Body></s:Envelope>";
        using var req = new HttpRequestMessage(HttpMethod.Post, control);
        req.Content = new StringContent(body, Encoding.UTF8, "text/xml");
        req.Headers.Add("SOAPAction", $"\"{service}#{action}\"");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var resp = Http.SendAsync(req, cts.Token).GetAwaiter().GetResult();
        return ((int)resp.StatusCode, resp.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult());
    }

    private static string? HttpString(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            return Http.GetStringAsync(url, cts.Token).GetAwaiter().GetResult();
        }
        catch { return null; }
    }

    /// <summary>Namespace-prefix tolerant extraction of a leaf element's
    /// text (the upnp.cpp xmlValue, ported; SOAP faults and router
    /// descriptions are far from uniformly prefixed).</summary>
    private static string XmlValue(string xml, string tag) =>
        Regex.Match(xml, $"<(?:\\w+:)?{tag}>([^<]*)</").Groups[1].Value.Trim();

    [GeneratedRegex("<(?:\\w+:)?service>(.*?)</(?:\\w+:)?service>", RegexOptions.Singleline)]
    private static partial Regex ServiceRx();
}
