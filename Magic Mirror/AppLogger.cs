using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Magic_Mirror
{
    public static class AppLogger
    {
        private static readonly object LogSync =
            new();

        private static readonly string LogDirectory =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData
                ),
                "Magic Mirror",
                "Logs"
            );

        private static string? logFilePath;

        public static string? CurrentLogFilePath =>
            logFilePath;

        public static void Initialize()
        {
            try
            {
                Directory.CreateDirectory(
                    LogDirectory
                );

                string timestamp =
                    DateTime.Now.ToString(
                        "yyyy-MM-dd_HHmmss"
                    );

                logFilePath =
                    Path.Combine(
                        LogDirectory,
                        $"MagicMirror-{timestamp}.log"
                    );

                Write(
                    "INFO",
                    "Magic Mirror logging started."
                );

                Write(
                    "INFO",
                    $"Process ID: {Environment.ProcessId}"
                );

                Write(
                    "INFO",
                    $"Operating system: {Environment.OSVersion}"
                );

                Write(
                    "INFO",
                    $"64-bit process: {Environment.Is64BitProcess}"
                );

                Write(
                    "INFO",
                    $"Application path: {AppContext.BaseDirectory}"
                );
            }
            catch
            {
                // Logging must never prevent Magic Mirror
                // from starting or functioning.
                logFilePath = null;
            }
        }

        public static void Info(
            string message)
        {
            Write(
                "INFO",
                message
            );
        }

        public static void Warning(
            string message)
        {
            Write(
                "WARNING",
                message
            );
        }

        public static void Error(
            string message)
        {
            Write(
                "ERROR",
                message
            );
        }

        public static void Error(
            string message,
            Exception exception)
        {
            Write(
                "ERROR",
                $"{message}{Environment.NewLine}" +
                $"{exception}"
            );
        }

        private static void Write(
            string level,
            string message)
        {
            string? filePath =
                logFilePath;

            if (string.IsNullOrWhiteSpace(
                    filePath))
            {
                return;
            }

            try
            {
                string line =
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] " +
                    $"[{level}] " +
                    message +
                    Environment.NewLine;

                lock (LogSync)
                {
                    File.AppendAllText(
                        filePath,
                        line,
                        Encoding.UTF8
                    );
                }

                Debug.Write(
                    line
                );
            }
            catch
            {
                // Never throw an exception because
                // diagnostic logging failed.
            }
        }
    }
}