using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Mycraft.Demo;

public static class DemoLog
{
    private static readonly ConcurrentQueue<string> Messages = new ConcurrentQueue<string>();
    private static readonly object Gate = new object();
    private static Timer? _timer;
    private static string? _path;

    public static void Initialize(string moduleFolder)
    {
        try
        {
            var directory = Path.Combine(moduleFolder, "Logs");
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, "mycraft.log");
            if (File.Exists(_path) && new FileInfo(_path).Length > 2 * 1024 * 1024)
                File.Copy(_path, _path + ".previous", true);
            File.WriteAllText(_path, "Mycraft session " + DateTimeOffset.Now.ToString("O") + Environment.NewLine);
            _timer = new Timer(_ => Flush(), null, 500, 500);
        }
        catch (Exception e) { Trace.TraceError("Mycraft logging unavailable: " + e.Message); }
    }

    public static void Info(string message)
    {
        Trace.TraceInformation("[Mycraft] " + message);
        if (_path != null && Messages.Count < 1024)
            Messages.Enqueue(DateTimeOffset.Now.ToString("O") + " " + message);
    }

    private static void Flush()
    {
        lock (Gate)
        {
            if (_path == null) return;
            var batch = new List<string>();
            while (Messages.TryDequeue(out var message)) batch.Add(message);
            if (batch.Count == 0) return;
            try { File.AppendAllLines(_path, batch); }
            catch (IOException e) { Trace.TraceError("Mycraft log write failed: " + e.Message); }
            catch (UnauthorizedAccessException e) { Trace.TraceError("Mycraft log access failed: " + e.Message); }
        }
    }

    public static void Shutdown() { _timer?.Dispose(); _timer = null; Flush(); }

    // One-time native initialization breadcrumbs must survive an immediate engine crash.
    public static void Diagnostic(string message) { Info(message); Flush(); }
}
