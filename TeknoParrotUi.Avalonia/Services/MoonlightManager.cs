using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TeknoParrotUi.Avalonia.Services;

public sealed class MoonlightCommandResult
{
    public int ExitCode { get; set; }
    public string StandardOutput { get; set; } = "";
    public string StandardError { get; set; } = "";

    public string GetBestError(string fallback)
    {
        if (!string.IsNullOrWhiteSpace(StandardError)) return StandardError.Trim();
        if (!string.IsNullOrWhiteSpace(StandardOutput)) return StandardOutput.Trim();
        return fallback;
    }
}

/// <summary>
/// Controls the separately-downloaded Moonlight portable at
/// &lt;TeknoParrot root&gt;\Moonlight\Moonlight.exe.
/// </summary>
public static class MoonlightManager
{
    public static string MoonlightDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Moonlight");

    public static string MoonlightExecutablePath =>
        Path.Combine(MoonlightDirectory, "Moonlight.exe");

    public static bool IsInstalled() => File.Exists(MoonlightExecutablePath);

    public static void Open()
    {
        EnsureWindows();
        EnsureInstalled();
        Process.Start(new ProcessStartInfo
        {
            FileName = MoonlightExecutablePath,
            WorkingDirectory = MoonlightDirectory,
            UseShellExecute = false
        });
    }

    public static async Task<MoonlightCommandResult> PairAsync(string host, string pin)
    {
        EnsureWindows();
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required.", nameof(host));
        if (string.IsNullOrWhiteSpace(pin)) throw new ArgumentException("PIN is required.", nameof(pin));
        EnsureInstalled();

        var startInfo = new ProcessStartInfo
        {
            FileName = MoonlightExecutablePath,
            Arguments = $"pair {Quote(host)} --pin {Quote(pin)}",
            WorkingDirectory = MoonlightDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.Environment["TEKNOPARROT_HEADLESS_PAIR"] = "1";

        using var pairProcess = Process.Start(startInfo);
        if (pairProcess == null)
            throw new InvalidOperationException("Moonlight pairing could not be started.");

        var deadline = DateTime.UtcNow.AddSeconds(60);
        MoonlightCommandResult? lastCheck = null;

        while (DateTime.UtcNow < deadline)
        {
            if (pairProcess.HasExited && pairProcess.ExitCode == 0)
                return Success("Pairing completed.");

            try
            {
                lastCheck = await RunCommandAsync($"list {Quote(host)}", TimeSpan.FromSeconds(5));
                if (lastCheck.ExitCode == 0)
                {
                    try { if (!pairProcess.HasExited) pairProcess.Kill(); } catch { }
                    return Success("Pairing completed.");
                }
            }
            catch (TimeoutException) { }
            catch { }

            await Task.Delay(500);
        }

        try { if (!pairProcess.HasExited) pairProcess.Kill(); } catch { }

        return new MoonlightCommandResult
        {
            ExitCode = 1,
            StandardOutput = lastCheck?.StandardOutput ?? "",
            StandardError = string.IsNullOrWhiteSpace(lastCheck?.StandardError)
                ? "Pairing was not confirmed within 60 seconds."
                : lastCheck.StandardError
        };
    }

    public static Task<IReadOnlyList<string>> ListAppsAsync(string host) =>
        ListAppsAsync(host, TimeSpan.FromSeconds(45));

    public static async Task<IReadOnlyList<string>> ListAppsAsync(string host, TimeSpan timeout)
    {
        EnsureWindows();
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required.", nameof(host));

        var result = await RunCommandAsync($"list {Quote(host)}", timeout);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(result.GetBestError("Moonlight failed to retrieve the application list."));

        return (result.StandardOutput ?? "")
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static Process StartStream(string host, string appName)
    {
        EnsureWindows();
        EnsureInstalled();
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required.", nameof(host));
        if (string.IsNullOrWhiteSpace(appName)) throw new ArgumentException("Application name is required.", nameof(appName));

        return Process.Start(new ProcessStartInfo
        {
            FileName = MoonlightExecutablePath,
            Arguments = $"stream {Quote(host)} {Quote(appName)}",
            WorkingDirectory = MoonlightDirectory,
            UseShellExecute = false,
            CreateNoWindow = false
        }) ?? throw new InvalidOperationException("Moonlight could not be started.");
    }

    public static Task<MoonlightCommandResult> QuitStreamAsync(string host)
    {
        EnsureWindows();
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required.", nameof(host));
        return RunCommandAsync($"quit {Quote(host)}", TimeSpan.FromSeconds(30));
    }

    public static void StopAll()
    {
        if (!OperatingSystem.IsWindows()) return;

        Process[] processes;
        try { processes = Process.GetProcessesByName("Moonlight"); }
        catch { return; }

        foreach (var process in processes)
        {
            try
            {
                if (process.HasExited) continue;
                var path = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(path)) continue;

                if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(MoonlightExecutablePath),
                    StringComparison.OrdinalIgnoreCase))
                    process.Kill();
            }
            catch { }
            finally { process.Dispose(); }
        }
    }

    private static async Task<MoonlightCommandResult> RunCommandAsync(string arguments, TimeSpan timeout)
    {
        EnsureInstalled();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = MoonlightExecutablePath,
                Arguments = arguments,
                WorkingDirectory = MoonlightDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        if (!process.Start()) throw new InvalidOperationException("Moonlight could not be started.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
            throw new TimeoutException($"Moonlight did not finish within {timeout.TotalSeconds:0} seconds.");
        }

        process.WaitForExit();
        return new MoonlightCommandResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString()
        };
    }

    private static MoonlightCommandResult Success(string message) => new()
    {
        ExitCode = 0,
        StandardOutput = message
    };

    private static void EnsureInstalled()
    {
        if (!IsInstalled())
            throw new FileNotFoundException(
                "Moonlight could not be found. Download the Moonlight portable and copy its folder into the TeknoParrot root as Moonlight.",
                MoonlightExecutablePath);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("TeknoParrot Moonlight management is currently available on Windows only.");
    }

    private static string Quote(string value) => "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
}
