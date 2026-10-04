using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

public sealed record MobileDeviceInfo(string Id, string Name, string FirstSeen, string LastSeen, bool Legacy);
public sealed record MobileAuthResult(string? DeviceId, string? DeviceToken = null, string? Error = null)
{ public override string ToString() => $"MobileAuthResult {{ DeviceId = {DeviceId}, Error = {Error}, DeviceToken = [redacted] }}"; }

/// Pairing identity and token hashes commit together: a failed key rotation
/// cannot leave a new key paired with an old device registry. Secrets never
/// enter AppSnapshot, diagnostic strings, notifications or ordinary logs.
public sealed class MobileIdentity
{
    private sealed record Device(string Id, string Name, string? TokenHash, string FirstSeen, string LastSeen);
    private sealed record Saved(int Version, string HostId, string HostToken, string PairingKey, string SecretKeyB64, List<Device> Devices);
    private readonly object sync = new();
    private readonly string path;
    private Saved state;
    private readonly List<DateTimeOffset> registrations = [];
    public string HostId => state.HostId;
    internal string HostToken => state.HostToken;
    internal string PairingKey => state.PairingKey;
    internal byte[] Secret => Convert.FromBase64String(state.SecretKeyB64);
    public string PublicKey => Convert.ToBase64String(RelayCipher.PublicKey(Secret));
    public string ServerId => Hash(state.HostToken);
    public IReadOnlyList<MobileDeviceInfo> Devices { get { lock (sync) return state.Devices.Select(d => new MobileDeviceInfo(d.Id, d.Name, d.FirstSeen, d.LastSeen, d.TokenHash is null)).ToArray(); } }
    public MobileIdentity(string directory)
    {
        SecureDirectory(directory); path = Path.Combine(directory, "mobile-identity.json");
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 128 * 1024 || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Invalid mobile identity file.");
            state = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path), Wire.Json) ?? throw new IOException("Invalid mobile identity.");
            if (state.Version != 1 || !Wire.Identifier(state.HostId) || !Regex.IsMatch(state.HostToken, "^[a-f0-9]{64}$") || !TokenShape(state.PairingKey) || Convert.FromBase64String(state.SecretKeyB64).Length != 32 || state.Devices.Count > 32 || state.Devices.Select(d => d.Id).Distinct().Count() != state.Devices.Count || state.Devices.Any(d => d.Id != "legacy" && !ClientId(d.Id) || d.Id == "legacy" && d.TokenHash is not null || d.Id != "legacy" && (d.TokenHash is null || !Regex.IsMatch(d.TokenHash, "^[a-f0-9]{64}$")) || d.Name.Length > 160 || !DateTimeOffset.TryParse(d.FirstSeen, out _) || !DateTimeOffset.TryParse(d.LastSeen, out _))) throw new IOException("Invalid mobile identity.");
        }
        else { state = new(1, Wire.Id(), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), RandomToken(32), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), []); Save(state); }
    }
    public static bool ClientId(string? value) => value is not null && Regex.IsMatch(value, "^[A-Za-z0-9_-]{22}$");
    private static bool TokenShape(string value) => Regex.IsMatch(value, "^[A-Za-z0-9_-]{43,128}$");
    private static string RandomToken(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static bool Equal(string first, string second) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(second));
    public MobileAuthResult Authenticate(JsonElement auth, bool allowLegacy)
    {
        lock (sync)
        {
            if (auth.Text("type") != "auth") return new(null, Error: "malformed");
            var id = auth.Text("clientId");
            if (auth.TryGetProperty("clientId", out _) && !ClientId(id)) return new(null, Error: "malformed");
            var presented = auth.Text("pairingKey");
            if (presented is null && auth.Text("deviceToken") is { Length: <= 256 } token)
            {
                var device = state.Devices.FirstOrDefault(d => d.Id == id);
                if (device?.TokenHash is null || !Equal(device.TokenHash, Hash(token))) return new(null, Error: "device-revoked");
                Touch(device); return new(device.Id);
            }
            if (presented is not { Length: <= 256 }) return new(null, Error: "malformed");
            if (!Equal(Hash(presented), Hash(state.PairingKey))) return new(null, Error: "pairing-key");
            var now = DateTimeOffset.UtcNow;
            if (id is null)
            {
                if (!allowLegacy) return new(null, Error: "legacy-refused");
                if (state.Devices.FirstOrDefault(d => d.Id == "legacy") is { } existing) Touch(existing);
                else if (state.Devices.Count >= 32) return new(null, Error: "device-limit");
                else Save(state with { Devices = state.Devices.Append(new Device("legacy", "Legacy app", null, Wire.Now(), Wire.Now())).ToList() });
                return new("legacy");
            }
            if (state.Devices.Any(d => d.Id == id && d.TokenHash is not null)) return new(null, Error: "device-conflict");
            registrations.RemoveAll(t => now - t > TimeSpan.FromHours(1));
            if (registrations.Count >= 8) return new(null, Error: "device-limit");
            var devices = state.Devices.ToList();
            if (devices.Count >= 32)
            {
                var old = devices.Where(d => DateTimeOffset.TryParse(d.LastSeen, out var last) && now - last > TimeSpan.FromDays(90)).OrderBy(d => d.LastSeen).FirstOrDefault();
                if (old is null) return new(null, Error: "device-limit"); devices.Remove(old);
            }
            var fresh = RandomToken(32); var name = Wire.Clean(auth.Text("clientName") ?? "Phone", 40);
            devices.Add(new(id, name, Hash(fresh), Wire.Now(), Wire.Now()));
            Save(state with { Devices = devices }); registrations.Add(now); return new(id, fresh);
        }
    }
    private void Touch(Device device)
    {
        if (!DateTimeOffset.TryParse(device.LastSeen, out var seen) || DateTimeOffset.UtcNow - seen > TimeSpan.FromMinutes(1))
            Save(state with { Devices = state.Devices.Select(d => d.Id == device.Id ? d with { LastSeen = Wire.Now() } : d).ToList() });
    }
    public void Rotate()
    {
        lock (sync) { Save(state with { PairingKey = RandomToken(32), Devices = [] }); registrations.Clear(); }
    }
    public bool Contains(string id, bool legacyAllowed) { lock (sync) return state.Devices.Any(d => d.Id == id && (d.TokenHash is not null || id == "legacy" && legacyAllowed)); }
    public string PairingUrl(string relay, string name)
    {
        lock (sync) return "mightyclaude://pair?v=2&sid=" + ServerId + "&pk=" + Uri.EscapeDataString(PublicKey.TrimEnd('=').Replace('+', '-').Replace('/', '_')) + "&relay=" + Uri.EscapeDataString(relay) + "&key=" + Uri.EscapeDataString(state.PairingKey) + "&name=" + Uri.EscapeDataString(Wire.Clean(name, 120));
    }
    private void Save(Saved next)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temp, options)) { JsonSerializer.Serialize(file, next, Wire.Json); file.Flush(true); }
            File.Move(temp, path, true); state = next;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static void SecureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Refusing linked mobile identity directory.");
        if (OperatingSystem.IsWindows())
        {
            var sid = WindowsIdentity.GetCurrent().User ?? throw new IOException("No Windows account identity.");
            var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false); security.SetOwner(sid);
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
