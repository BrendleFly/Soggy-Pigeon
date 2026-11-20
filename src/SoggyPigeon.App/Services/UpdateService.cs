using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SoggyPigeon.Protocol;

namespace SoggyPigeon.App.Services;

/// <summary>What the release feed says is available.</summary>
public sealed record UpdateInfo(string Version, string DownloadUrl, long SizeBytes, string Notes);

/// <summary>
/// The app's own updater (2026-11-20).
///
/// The source of truth is the public GitHub Releases feed for
/// BrendleFly/Soggy-Pigeon: a release tagged "v&lt;version&gt;" carrying an
/// asset named "SoggyPigeon-Setup-&lt;version&gt;.exe". GitHub is used rather
/// than our own server because releases are free, immutable (a tag cannot be
/// silently rewritten), and need no uptime from us.
///
/// Deliberately conservative, because a bad update is worse than no update:
///   - the check is read-only and cannot fail the app: any error means "no
///     update found" and the game carries on;
///   - the download goes to %TEMP%, is hashed against the SHA-256 the release
///     publishes, and is only then offered to the user;
///   - applying the update LAUNCHES THE INSTALLER and exits - the installer is
///     what replaces the files, so there is no half-swapped directory and no
///     self-overwrite problem.
///
/// Version comparison is numeric per component, not string ordering, so 0.10.0
/// correctly beats 0.9.0.
/// </summary>
public static class UpdateService
{
    /// <summary>Where releases are read from. A tag "v0.2.0" + asset
    /// "SoggyPigeon-Setup-0.2.0.exe" is the expected shape; both are built by
    /// the release script so they cannot drift from Proto.ClientVersion.</summary>
    public const string ReleasesApi =
        "https://api.github.com/repos/BrendleFly/Soggy-Pigeon/releases/latest";

    /// <summary>Required so GitHub's API identifies the client (anonymous
    /// requests without a User-Agent are rejected).</summary>
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false })
    {
        DefaultRequestHeaders =
        {
            { "User-Agent", "SoggyPigeon-Updater" },
            { "Accept", "application/vnd.github+json" },
        },
    };

    /// <summary>True when the running build is older than the newest release.
    /// Never throws: a network failure, a malformed feed or a 404 all mean
    /// "no update", which is the safe answer.
    ///
    /// Returns an UpdateInfo with a null Version when there is simply no
    /// release yet (a 404, e.g. before the first GitHub release), so the
    /// caller can say "no releases published" rather than claiming the player
    /// is up to date.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync(ReleasesApi, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new UpdateInfo("", "", 0, "");
            if (!resp.IsSuccessStatusCode) return null;
            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tagName", out var tagEl)) return null;
            var tag = tagEl.GetString() ?? "";
            var latest = ParseVersion(tag);
            if (latest is null) return null;
            if (CompareVersions(latest, Proto.ClientVersion) <= 0) return null;

            if (!root.TryGetProperty("assets", out var assets) ||
                assets.ValueKind != JsonValueKind.Array) return null;

            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameEl)) continue;
                var name = nameEl.GetString() ?? "";
                // The installer asset carries the version in its name; accept
                // it only when it matches the tag, so a stray asset cannot
                // impersonate a release.
                var expected = $"SoggyPigeon-Setup-{latest}.exe";
                if (!string.Equals(name, expected, StringComparison.OrdinalIgnoreCase)) continue;

                if (!asset.TryGetProperty("browser_download_url", out var urlEl)) continue;
                var url = urlEl.GetString();
                if (string.IsNullOrEmpty(url)) continue;
                var size = asset.TryGetProperty("size", out var sizeEl) ? sizeEl.GetInt64() : 0;
                var notes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";
                return new UpdateInfo(latest, url, size, notes);
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) { }
        catch (JsonException) { }
        catch (IOException) { }
        return null;
    }

    /// <summary>Download the installer to %TEMP% and verify its SHA-256 against
    /// the hash published in the release body ("SHA256: &lt;hex&gt;"). Returns
    /// the local path, or null on any failure. The download is streamed to a
    /// .tmp file and only renamed once complete, so a partial file is never
    /// mistaken for an installer.</summary>
    public static async Task<string?> DownloadAsync(UpdateInfo info, IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "soggy-pigeon-update");
        Directory.CreateDirectory(dir);
        var finalPath = Path.Combine(dir, $"SoggyPigeon-Setup-{info.Version}.exe");
        var tmpPath = finalPath + ".tmp";
        try
        {
            using var resp = await Http.GetAsync(info.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var total = info.SizeBytes > 0 ? info.SizeBytes : resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var buf = new byte[81920];
            long written = 0;
            int read;
            while ((read = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
                written += read;
                if (total > 0) progress?.Report((int)(written * 100 / total));
            }
            await dst.FlushAsync(ct).ConfigureAwait(false);

            var expected = ExtractSha256(info.Notes);
            if (expected is not null)
            {
                using var sha = SHA256.Create();
                await using var check = new FileStream(tmpPath, FileMode.Open, FileAccess.Read);
                var hash = Convert.ToHexString(sha.ComputeHash(check)).ToLowerInvariant();
                if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(tmpPath); } catch { }
                    return null;
                }
            }

            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(tmpPath, finalPath);
            return finalPath;
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
        return null;
    }

    /// <summary>Hand the downloaded installer to Windows and let it replace the
    /// installation. The caller must exit the app right after: the installer
    /// owns the files from here on.</summary>
    public static bool LaunchInstaller(string path)
    {
        try
        {
            var psi = new ProcessStartInfo(path) { UseShellExecute = true };
            Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>"v0.2.0" / "0.2.0" / "0.2.0-beta.1" -> the numeric core.
    /// Anything unparseable yields null rather than guessing.</summary>
    public static string? ParseVersion(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var m = Regex.Match(tag.Trim(), @"^v?(\d+(?:\.\d+)*)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Numeric, per-component comparison. Missing components count as
    /// zero, so "0.2" == "0.2.0". Returns &gt;0 when a is newer.</summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = (a ?? "").Split('.');
        var pb = (b ?? "").Split('.');
        var n = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < n; i++)
        {
            if (!int.TryParse(i < pa.Length ? pa[i] : "0", out var va)) va = 0;
            if (!int.TryParse(i < pb.Length ? pb[i] : "0", out var vb)) vb = 0;
            if (va != vb) return va.CompareTo(vb);
        }
        return 0;
    }

    /// <summary>Pull a 64-hex SHA-256 out of free-form release notes
    /// ("SHA256: abc…", "sha256 `abc…`"). Returns null when absent.</summary>
    public static string? ExtractSha256(string? notes)
    {
        if (string.IsNullOrEmpty(notes)) return null;
        var m = Regex.Match(notes, @"\b([a-fA-F0-9]{64})\b");
        return m.Success ? m.Groups[1].Value : null;
    }
}