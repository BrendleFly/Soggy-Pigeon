using System.Text;

namespace SoggyPigeon.App.Services;

/// <summary>One-line-per-event trace of the connection steps a match takes
/// (UPnP result, punch outcome, boot choice): the owner's screen shows only
/// the banner, and internet-play failures need the numbers afterwards. Same
/// spirit as the server's PIGEON_SERVER_DEBUG rendezvous log — always on,
/// tiny, best-effort.</summary>
public static class NetLog
{
    private static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sp-net.log");

    public static void Write(string line)
    {
        try
        {
            File.AppendAllText(Path,
                $"{DateTime.Now:HH:mm:ss.fff} {line}\n", Encoding.UTF8);
        }
        catch { /* a log must never break the match */ }
    }
}
