using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace DynamicIsland;

public static class SelfUpdater
{
    private const long MinPlausibleExeBytes = 5L * 1024 * 1024;

    public static string UrgentUpdateFlagPath => Path.Combine(UpdateDir, "urgent-update-applied.flag");

    private static string UpdateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CurrencyIsland", "update");

    private static string LogPath => Path.Combine(UpdateDir, "update.log");

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(UpdateDir);
            var log = new FileInfo(LogPath);
            if (log.Exists && log.Length > 256 * 1024) log.Delete();
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    public static void CleanupLeftovers()
    {
        try
        {
            var currentExePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(currentExePath))
            {
                var oldPath = currentExePath + ".old";
                if (File.Exists(oldPath)) File.Delete(oldPath);
            }

            var staged = Path.Combine(UpdateDir, "CurrencyIsland.new.exe");
            if (File.Exists(staged)) File.Delete(staged);
        }
        catch
        {
        }
    }

    public static async Task DownloadAndRestartAsync(string downloadUrl, IProgress<double>? progress = null, bool urgent = false)
    {
        var currentExePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExePath))
            throw new InvalidOperationException("Could not determine own exe path.");

        Directory.CreateDirectory(UpdateDir);
        var newExePath = Path.Combine(UpdateDir, "CurrencyIsland.new.exe");
        Log($"update start, current={currentExePath}");

        long expectedBytes;
        using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CurrencyIsland-UpdateChecker");
            using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            expectedBytes = response.Content.Headers.ContentLength ?? -1L;

            await using var httpStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(newExePath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await httpStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read));
                totalRead += read;
                if (expectedBytes > 0)
                    progress?.Report((double)totalRead / expectedBytes);
            }
        }

        var downloaded = new FileInfo(newExePath);
        if (downloaded.Length < MinPlausibleExeBytes || (expectedBytes > 0 && downloaded.Length != expectedBytes))
        {
            Log($"download rejected, size={downloaded.Length}, expected={expectedBytes}");
            File.Delete(newExePath);
            throw new InvalidDataException("Downloaded update is incomplete.");
        }

        var oldPath = currentExePath + ".old";
        try
        {
            if (File.Exists(oldPath)) File.Delete(oldPath);
            File.Move(currentExePath, oldPath);
        }
        catch (Exception ex)
        {
            Log($"cannot move current exe aside: {ex}");
            throw;
        }

        try
        {
            File.Copy(newExePath, currentExePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log($"cannot place new exe, rolling back: {ex}");
            try
            {
                if (File.Exists(currentExePath)) File.Delete(currentExePath);
                File.Move(oldPath, currentExePath);
            }
            catch (Exception rollbackEx)
            {
                Log($"rollback failed: {rollbackEx}");
            }
            throw;
        }

        try { File.Delete(newExePath); } catch { }

        if (urgent)
        {
            try { await File.WriteAllTextAsync(UrgentUpdateFlagPath, ""); } catch { }
        }

        Log("swap done, launching new exe");
        Process.Start(new ProcessStartInfo
        {
            FileName = currentExePath,
            WorkingDirectory = Path.GetDirectoryName(currentExePath) ?? "",
            UseShellExecute = true,
        });

        System.Windows.Application.Current.Shutdown();
    }
}
