using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace TeknoParrotUi.Common.GameLaunch
{
    internal static class GoldenTeePlayerDefaultsLoader
    {
        private const string ProfileName = "GoldenTeeLive2019";
        private const string NativeDirectoryName = "GoldenTee";
        private const string HelperFileName = "GoldenTeePlayerDefaults.dll";
        private const string BootstrapFileName = "GoldenTeePlayerDefaultsBootstrap.dll";

        public static bool ShouldLoad(GameProfile profile)
        {
            return OperatingSystem.IsWindows() &&
                   profile != null &&
                   string.Equals(
                       profile.ProfileName,
                       ProfileName,
                       StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryLoad(
            GameProfile profile,
            Process targetProcess,
            Action<string> log)
        {
            if (!ShouldLoad(profile))
                return true;

            if (targetProcess == null)
            {
                log?.Invoke("[GoldenTeeDefaults] ERROR: BudgieLoader process was not available.");
                return false;
            }

            try
            {
                if (targetProcess.HasExited)
                {
                    log?.Invoke("[GoldenTeeDefaults] ERROR: BudgieLoader exited before helper load.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"[GoldenTeeDefaults] ERROR: Could not inspect BudgieLoader: {ex.Message}");
                return false;
            }

            var nativeDirectory = Path.Combine(
                AppContext.BaseDirectory,
                "Native",
                NativeDirectoryName);

            var helperPath = Path.Combine(nativeDirectory, HelperFileName);
            var bootstrapPath = Path.Combine(nativeDirectory, BootstrapFileName);

            if (!File.Exists(helperPath))
            {
                log?.Invoke($"[GoldenTeeDefaults] ERROR: Missing {helperPath}");
                return false;
            }

            if (!File.Exists(bootstrapPath))
            {
                log?.Invoke($"[GoldenTeeDefaults] ERROR: Missing {bootstrapPath}");
                return false;
            }

            var windowsDirectory =
                Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            var rundll32Path = Path.Combine(
                windowsDirectory,
                "SysWOW64",
                "rundll32.exe");

            if (!File.Exists(rundll32Path))
            {
                log?.Invoke($"[GoldenTeeDefaults] ERROR: 32-bit rundll32.exe not found: {rundll32Path}");
                return false;
            }

            var info = new ProcessStartInfo
            {
                FileName = rundll32Path,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            info.ArgumentList.Add(bootstrapPath + ",Inject");
            info.ArgumentList.Add(
                targetProcess.Id.ToString(CultureInfo.InvariantCulture));

            log?.Invoke(
                $"[GoldenTeeDefaults] Waiting for Golden Tee image in BudgieLoader PID {targetProcess.Id}...");

            using var bootstrap = Process.Start(info);

            if (bootstrap == null)
            {
                log?.Invoke("[GoldenTeeDefaults] ERROR: Could not start x86 bootstrap.");
                return false;
            }

            if (!bootstrap.WaitForExit(35000))
            {
                try
                {
                    bootstrap.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                log?.Invoke("[GoldenTeeDefaults] ERROR: x86 bootstrap timed out.");
                return false;
            }

            if (bootstrap.ExitCode != 0)
            {
                log?.Invoke(
                    $"[GoldenTeeDefaults] ERROR: x86 bootstrap failed with code {bootstrap.ExitCode}.");
                return false;
            }

            log?.Invoke(
                $"[GoldenTeeDefaults] GoldenTeePlayerDefaults.dll loaded into BudgieLoader PID {targetProcess.Id}.");

            return true;
        }
    }
}
