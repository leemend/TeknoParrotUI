using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TeknoParrotUi.Avalonia.Services;

public sealed class SunshineStatus
{
    public bool Status { get; set; }
    public bool Running { get; set; }
    public bool Managed { get; set; }
    public string Version { get; set; } = "";
    public string Platform { get; set; } = "";
    public string ConnectionMode { get; set; } = "closed";
    public bool ConnectionOpen { get; set; }
    public int ActiveSessions { get; set; }
    public int PairedClients { get; set; }
    public bool PairingPending { get; set; }
}

public sealed class SunshineClientInfo
{
    public string Uuid { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Connected { get; set; }

    public string DisplayName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Name) ? "Moonlight Client" : Name;
            var state = Connected ? "Connected" : "Paired • Offline";
            if (!Enabled) state += " • Disabled";
            return $"{name} — {state}";
        }
    }

    public override string ToString() => DisplayName;
}

public static class SunshineManager
{
    private const string ManagedApiBaseUrl = "https://127.0.0.1:47990";
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public static string SunshineDirectory => Path.Combine(AppContext.BaseDirectory, "Sunshine");
    public static string SunshineExecutablePath => Path.Combine(SunshineDirectory, "sunshine.exe");
    public static bool IsInstalled() => File.Exists(SunshineExecutablePath);

    public static bool IsRunning()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return Process.GetProcessesByName("sunshine").Any(p => !p.HasExited); }
        catch { return false; }
    }

    public static void Start()
    {
        EnsureWindows();
        if (!IsInstalled()) throw new FileNotFoundException("Sunshine could not be found.", SunshineExecutablePath);
        if (IsRunning()) return;

        Process.Start(new ProcessStartInfo
        {
            FileName = SunshineExecutablePath,
            Arguments = $"--managed --parent-pid {Process.GetCurrentProcess().Id}",
            WorkingDirectory = SunshineDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    public static async Task<SunshineStatus> GetStatusAsync()
    {
        using var doc = await GetJsonAsync("/api/managed/status");
        var root = doc.RootElement;
        return new SunshineStatus
        {
            Status = Bool(root, "status"),
            Running = Bool(root, "running"),
            Managed = Bool(root, "managed"),
            Version = Str(root, "version"),
            Platform = Str(root, "platform"),
            ConnectionMode = string.IsNullOrWhiteSpace(Str(root, "connection_mode")) ? "closed" : Str(root, "connection_mode"),
            ConnectionOpen = Bool(root, "connection_open"),
            ActiveSessions = Int(root, "active_sessions"),
            PairedClients = Int(root, "paired_clients"),
            PairingPending = Bool(root, "pairing_pending")
        };
    }

    public static async Task<IReadOnlyList<SunshineClientInfo>> GetClientsAsync()
    {
        using var doc = await GetJsonAsync("/api/managed/clients");
        var list = new List<SunshineClientInfo>();
        if (!doc.RootElement.TryGetProperty("clients", out var clients)) return list;

        if (clients.ValueKind == JsonValueKind.Array)
            foreach (var client in clients.EnumerateArray()) list.Add(ParseClient(client, ""));
        else if (clients.ValueKind == JsonValueKind.Object)
            foreach (var item in clients.EnumerateObject()) list.Add(ParseClient(item.Value, item.Name));

        return list;
    }

    public static async Task SetConnectionModeAsync(string mode)
    {
        if (!string.Equals(mode, "open", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "closed", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Managed Sunshine connection mode must be either 'open' or 'closed'.", nameof(mode));

        using var _ = await PostJsonAsync("/api/managed/connection-mode",
            JsonSerializer.Serialize(new { mode = mode.ToLowerInvariant() }));
    }

    public static async Task PairAsync(string pin, string name)
    {
        if (string.IsNullOrWhiteSpace(pin) || pin.Length != 4 || !pin.All(char.IsDigit))
            throw new ArgumentException("Pairing PIN must contain exactly 4 digits.", nameof(pin));

        using var response = await PostJsonAsync("/api/managed/pair",
            JsonSerializer.Serialize(new { pin, name = name ?? "" }));

        if (!Bool(response.RootElement, "status"))
            throw new InvalidOperationException("Sunshine rejected the pairing request.");
    }

    public static async Task UnpairAsync(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid)) throw new ArgumentException("A client UUID is required.", nameof(uuid));

        using var response = await PostJsonAsync("/api/managed/unpair",
            JsonSerializer.Serialize(new { uuid }));

        if (!Bool(response.RootElement, "status"))
            throw new InvalidOperationException("Sunshine could not unpair the selected client.");
    }

    public static async Task DisconnectAllAsync()
    {
        using var _ = await PostAsync("/api/managed/disconnect-all");
    }

    public static async Task StopAsync()
    {
        if (!IsRunning()) return;

        try { using var _ = await PostAsync("/api/managed/shutdown"); }
        catch { }

        if (await WaitForRunningStateAsync(false, TimeSpan.FromSeconds(5))) return;
        ForceStopBundledProcess();
        await WaitForRunningStateAsync(false, TimeSpan.FromSeconds(3));
    }

    public static async Task RestartAsync()
    {
        await StopAsync();
        Start();

        var deadline = DateTime.UtcNow.AddSeconds(8);
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var status = await GetStatusAsync();
                if (status.Running && status.Managed) return;
            }
            catch (Exception ex) { lastError = ex; }

            await Task.Delay(200);
        }

        throw new InvalidOperationException("Sunshine restarted, but its managed API did not become available.", lastError);
    }

    public static async Task<bool> WaitForRunningStateAsync(bool expectedRunning, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsRunning() == expectedRunning) return true;
            await Task.Delay(100);
        }
        return IsRunning() == expectedRunning;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
            {
                var uri = request?.RequestUri;
                if (uri != null && uri.Port == 47990 &&
                    (string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)))
                    return true;

                return errors == System.Net.Security.SslPolicyErrors.None;
            }
        };

        return new HttpClient(handler)
        {
            BaseAddress = new Uri(ManagedApiBaseUrl),
            Timeout = TimeSpan.FromSeconds(3)
        };
    }

    private static async Task<JsonDocument> GetJsonAsync(string path)
    {
        using var response = await HttpClient.GetAsync(path);
        return await ReadJsonAsync(response);
    }

    private static async Task<JsonDocument> PostAsync(string path)
    {
        using var response = await HttpClient.PostAsync(path, null);
        return await ReadJsonAsync(response);
    }

    private static async Task<JsonDocument> PostJsonAsync(string path, string payload)
    {
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await HttpClient.PostAsync(path, content);
        return await ReadJsonAsync(response);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Sunshine managed API returned {(int)response.StatusCode}: {(string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body)}");

        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    private static SunshineClientInfo ParseClient(JsonElement client, string fallbackUuid) => new()
    {
        Uuid = First(client, "uuid", "uniqueid", "unique_id", "id") ?? fallbackUuid,
        Name = First(client, "name", "display_name", "friendly_name", "hostname") ?? "",
        Enabled = !client.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.False,
        Connected = client.TryGetProperty("connected", out var connected) && connected.ValueKind == JsonValueKind.True
    };

    private static string? First(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var token) && token.ValueKind != JsonValueKind.Null &&
                !string.IsNullOrWhiteSpace(token.ToString()))
                return token.ToString();
        return null;
    }

    private static bool Bool(JsonElement e, string n) =>
        e.TryGetProperty(n, out var t) && t.ValueKind is JsonValueKind.True or JsonValueKind.False && t.GetBoolean();

    private static int Int(JsonElement e, string n) =>
        e.TryGetProperty(n, out var t) && t.TryGetInt32(out var v) ? v : 0;

    private static string Str(JsonElement e, string n) =>
        e.TryGetProperty(n, out var t) ? t.ToString() : "";

    private static void ForceStopBundledProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        Process[] processes;
        try { processes = Process.GetProcessesByName("sunshine"); }
        catch { return; }

        foreach (var process in processes)
        {
            try
            {
                if (process.HasExited) continue;
                var path = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(path)) continue;

                if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(SunshineExecutablePath),
                    StringComparison.OrdinalIgnoreCase))
                    process.Kill();
            }
            catch { }
            finally { process.Dispose(); }
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("TeknoParrot Sunshine management is currently available on Windows only.");
    }
}
