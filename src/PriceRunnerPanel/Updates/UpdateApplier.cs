using System.Globalization;

namespace PriceRunnerPanel.Updates;

/// <summary>
/// The hand-over step of an update, run by the <em>new</em> copy from its staging folder after the old app has quit:
/// move the installed folder aside as "&lt;folder&gt;.previous", copy the new version in, and start it. If anything fails,
/// the previous folder is put back and started, so a failed update never leaves the user without a working app.
/// The previous version stays next to the install folder for a manual rollback until the next update.
/// </summary>
public static class UpdateApplier
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Handles <c>--apply-update</c> before anything else starts. Returns false for a normal launch.</summary>
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args is not [UpdateArguments.Apply, ..])
        {
            return false;
        }

        var target = Value(args, UpdateArguments.Target);
        var from = Value(args, UpdateArguments.From) ?? "";
        if (target is null || !int.TryParse(Value(args, UpdateArguments.WaitPid), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
        {
            Log("Update hand-over started with incomplete arguments; nothing was changed.");
            exitCode = 1;
            return true;
        }

        exitCode = Apply(AppContext.BaseDirectory, target, pid, from);
        return true;
    }

    private static int Apply(string stagedDirectory, string targetDirectory, int waitForPid, string fromVersion)
    {
        var staged = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagedDirectory));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        var backup = target + ".previous";

        // Only ever replace a folder that is itself a release install, from a staged copy outside it.
        if (string.Equals(staged, target, StringComparison.OrdinalIgnoreCase)
            || staged.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(staged, AppInfo.ExecutableName))
            || InstallInfo.ReadManifestVersion(target) is null
            || InstallInfo.WhyNotReplaceable(target) is not null)
        {
            Log($"Refusing to update '{target}' from '{staged}' ({InstallInfo.WhyNotReplaceable(target) ?? "not a release folder"}).");
            if (WaitForExit(waitForPid))
            {
                Restart(target, UpdateFailures.Refused); // the old app has quit for the update: bring it back
            }

            return 1;
        }

        if (!WaitForExit(waitForPid))
        {
            Log($"The app (process {waitForPid}) didn't quit; the update was not applied.");
            return 2;
        }

        try
        {
            if (Directory.Exists(backup))
            {
                Retry(() => Directory.Delete(backup, recursive: true));
            }

            Retry(() => Directory.Move(target, backup)); // files can stay locked briefly after exit (antivirus, indexer)
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Couldn't move '{target}' aside ({ex.Message}); the update was not applied.");
            Restart(target, UpdateFailures.CouldNotMove);
            return 3;
        }

        try
        {
            CopyDirectory(staged, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Copying the new version failed ({ex.Message}); restoring the previous version.");
            try
            {
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }

                Directory.Move(backup, target);
            }
            catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
            {
                Log($"Restoring failed too ({restore.Message}). The previous version is in '{backup}'.");
                Restart(backup, UpdateFailures.RestoreFailed);
                return 5;
            }

            Restart(target, UpdateFailures.CopyFailed);
            return 4;
        }

        Log($"Updated '{target}' from {fromVersion}; the previous version is in '{backup}'.");
        ProcessLauncher.Start(Path.Combine(target, AppInfo.ExecutableName), [UpdateArguments.UpdatedFrom, fromVersion]);
        return 0;
    }

    /// <summary>Starts the previous version again and tells it why, so the user isn't silently offered the same update.</summary>
    private static void Restart(string folder, string failure) =>
        ProcessLauncher.Start(Path.Combine(folder, AppInfo.ExecutableName), [UpdateArguments.UpdateFailed, failure]);

    private static void Log(string message) => AppInfo.Log(message, "update.log");

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool WaitForExit(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.WaitForExit(ExitTimeout);
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }
    }

    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 10)
            {
                Thread.Sleep(500);
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }
}
