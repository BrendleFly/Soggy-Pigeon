using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SoggyPigeon.Server;

/// <summary>
/// The only state the v1 server persists (§6.1): token-hash → screen-name.
/// Stored as JSON at data/known-tokens.json; raw tokens are never written.
/// </summary>
public sealed class TokenStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, string> _byTokenHash;

    public TokenStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "known-tokens.json");
        _byTokenHash = Load();
    }

    /// <summary>Record (or rename) the screen name for a token. Returns the stored name.</summary>
    public string Remember(string token, string name)
    {
        var hash = Hash(token);
        lock (_gate)
        {
            _byTokenHash[hash] = name;
            Save();
            return _byTokenHash[hash];
        }
    }

    public bool TryLookup(string token, out string name)
    {
        var hash = Hash(token);
        lock (_gate) return _byTokenHash.TryGetValue(hash, out name!);
    }

    static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path))
                       ?? new Dictionary<string, string>();
        }
        catch { /* corrupt store: start fresh, tokens re-register on connect */ }
        return new Dictionary<string, string>();
    }

    void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_byTokenHash, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }
}
