using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace DynamicIsland;

internal static class LogBundle
{
    private static readonly string[] Files =
    {
        "resize-trace.log",
        "debug.log",
        "settings.json",
        "position.json",
        Path.Combine("update", "update.log")
    };

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CurrencyIsland");

    private static string SystemInfo(double dpiScale)
    {
        var info = new StringBuilder();
        info.AppendLine($"Version: {AppVersion.Current}");
        info.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        info.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        info.AppendLine($"Display scale: {dpiScale * 100:0}%");
        info.AppendLine($"Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        return info.ToString();
    }

    private static string Tail(string name, int lines)
    {
        var source = Path.Combine(Folder, name);
        if (!File.Exists(source)) return "(missing)";
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(input);
            var queue = new System.Collections.Generic.Queue<string>();
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                queue.Enqueue(line);
                if (queue.Count > lines) queue.Dequeue();
            }
            return string.Join(Environment.NewLine, queue);
        }
        catch
        {
            return "(unreadable)";
        }
    }

    public static string BuildText(double dpiScale)
    {
        var text = new StringBuilder();
        text.AppendLine(SystemInfo(dpiScale));
        text.AppendLine("--- settings.json ---");
        text.AppendLine(Tail("settings.json", 40));
        text.AppendLine("--- position.json ---");
        text.AppendLine(Tail("position.json", 40));
        text.AppendLine("--- debug.log (last 40) ---");
        text.AppendLine(Tail("debug.log", 40));
        text.AppendLine("--- update.log (last 120) ---");
        text.AppendLine(Tail(Path.Combine("update", "update.log"), 120));
        text.AppendLine("--- resize-trace.log (last 150) ---");
        text.AppendLine(Tail("resize-trace.log", 150));
        return text.ToString();
    }

    public static string Build(double dpiScale)
    {
        var path = Path.Combine(Path.GetTempPath(), "CurrencyIsland-logs.zip");
        if (File.Exists(path)) File.Delete(path);

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        var infoEntry = zip.CreateEntry("info.txt", CompressionLevel.Optimal);
        using (var writer = new StreamWriter(infoEntry.Open(), new UTF8Encoding(false)))
            writer.Write(SystemInfo(dpiScale));

        foreach (var name in Files)
        {
            var source = Path.Combine(Folder, name);
            if (!File.Exists(source)) continue;
            try
            {
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var entry = zip.CreateEntry(Path.GetFileName(name), CompressionLevel.Optimal);
                using var output = entry.Open();
                input.CopyTo(output);
            }
            catch
            {
            }
        }

        return path;
    }
}
