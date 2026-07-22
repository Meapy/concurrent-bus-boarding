using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Unity.Entities;
using UnityEngine;

namespace ConcurrentBusBoarding
{
    // ponytail: Compile calls only into local diagnostic builds; Paradox releases retain no logging overhead.
    internal static class CrashBreadcrumbs
    {
        private const int MaximumLines = 2000;
        private static readonly object s_Lock = new object();
        private static StreamWriter s_Writer;
        private static int s_Line;

        internal static string FilePath { get; private set; }

        [Conditional("CBB_DIAGNOSTICS")]
        internal static void Start()
        {
            lock (s_Lock)
            {
                Close();
                try
                {
                    string directory = Path.Combine(Application.persistentDataPath, "Logs");
                    Directory.CreateDirectory(directory);
                    FilePath = Path.Combine(directory, "ConcurrentBusBoarding-breadcrumbs.log");
                    s_Writer = new StreamWriter(
                        new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                        new UTF8Encoding(false))
                    {
                        AutoFlush = true
                    };
                    s_Line = 0;
                    WriteUnsafe("START diagnostic=boarding-crash-v6");
                }
                catch
                {
                    Close();
                }
            }
        }

        [Conditional("CBB_DIAGNOSTICS")]
        internal static void Write(string message)
        {
            lock (s_Lock)
            {
                try
                {
                    if (s_Writer == null || s_Line > MaximumLines)
                        return;
                    if (s_Line == MaximumLines)
                        message = "CAP_REACHED";
                    WriteUnsafe(message);
                }
                catch
                {
                    Close();
                }
            }
        }

        internal static string Id(Entity entity) => entity == Entity.Null
            ? "null"
            : $"{entity.Index}:{entity.Version}";

        [Conditional("CBB_DIAGNOSTICS")]
        internal static void Stop()
        {
            lock (s_Lock)
            {
                try
                {
                    if (s_Writer != null)
                        WriteUnsafe("STOP");
                }
                catch
                {
                }
                Close();
            }
        }

        private static void WriteUnsafe(string message)
        {
            s_Writer.WriteLine($"{s_Line++:D4} {DateTime.UtcNow:O} {message}");
        }

        private static void Close()
        {
            try
            {
                s_Writer?.Dispose();
            }
            catch
            {
            }
            s_Writer = null;
        }
    }
}
