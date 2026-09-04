using System;
using System.IO;
using System.Threading;

namespace TeknoParrotUi.Common.InputListening
{
    internal static class GoldenTeeRemoteInputTrace
    {
        private static readonly object Sync = new();
        private static readonly string PathName =
            Path.Combine(
                AppContext.BaseDirectory,
                "GT-remote-input-trace.txt");

        private static void EnsureHeader()
        {
            if (File.Exists(PathName) &&
                new FileInfo(PathName).Length > 0)
                return;

            File.WriteAllText(
                PathName,
                $"Golden Tee Remote Input Trace\r\n" +
                $"Started: {DateTime.Now:O}\r\n" +
                $"PID: {Environment.ProcessId}\r\n\r\n");
        }

        public static void Reset()
        {
            try
            {
                lock (Sync)
                {
                    // Do NOT truncate the file here. Provider-level events can
                    // occur before InputListenerXInput starts, and we need to
                    // preserve those startup markers.
                    EnsureHeader();

                    File.AppendAllText(
                        PathName,
                        $"{DateTime.Now:HH:mm:ss.fff} " +
                        $"T{Thread.CurrentThread.ManagedThreadId:D2} " +
                        $"[TRACE-SESSION] XInput listener started\r\n");
                }
            }
            catch
            {
            }
        }

        public static void Write(
            string stage,
            string message)
        {
            try
            {
                lock (Sync)
                {
                    EnsureHeader();

                    File.AppendAllText(
                        PathName,
                        $"{DateTime.Now:HH:mm:ss.fff} " +
                        $"T{Thread.CurrentThread.ManagedThreadId:D2} " +
                        $"[{stage}] {message}\r\n");
                }
            }
            catch
            {
            }
        }
    }
}
