using System.Text.Json;

namespace PriceRunnerPanel.Updates;

/// <summary>
/// What this copy of the app is and where it runs from. Only a copy extracted from a release zip (which carries
/// <see cref="ManifestFile"/>, written by scripts/publish-windows.ps1) can update itself; a build from source never
/// overwrites its own output folder.
/// </summary>
public sealed record InstallInfo(AppVersion Version, string InstallDirectory, bool IsPortableRelease, string? UpdatedFrom = null, string? UpdateFailed = null)
{
    public const string ManifestFile = "release.json";

    public static InstallInfo Detect(IReadOnlyList<string> args)
    {
        AppVersion.TryParse(AppInfo.Version, out var version);
        var directory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var portable = ReadManifestVersion(directory) is { } manifest && manifest == version;
        return new InstallInfo(version, directory, portable, Value(args, UpdateArguments.UpdatedFrom), FailureMessage(Value(args, UpdateArguments.UpdateFailed), version));
    }

    /// <summary>What to tell the user after the updater put this version back (see <see cref="UpdateApplier"/>).</summary>
    public static string? FailureMessage(string? code, AppVersion version)
    {
        if (code is null)
        {
            return null;
        }

        var reason = code switch
        {
            UpdateFailures.Refused => "the downloaded copy wasn't usable",
            UpdateFailures.CouldNotMove => "the app's folder couldn't be moved aside, probably because a file in it was in use",
            UpdateFailures.CopyFailed => "copying the new version failed, so the previous one was put back",
            UpdateFailures.RestoreFailed => "copying the new version failed and the previous version is running from its backup folder",
            _ => "something went wrong",
        };
        return $"The last update couldn't be applied: {reason}. You're still on version {version}. Details are in logs\\update.log in the data folder.";
    }

    /// <summary>
    /// Whether <paramref name="directory"/> may be replaced by an update: it is a release folder named like the app's,
    /// and every file in it is one its manifest says the release shipped. A folder holding anything else (the zip
    /// extracted into Downloads, say) is never moved or deleted.
    /// </summary>
    public static string? WhyNotReplaceable(string directory)
    {
        var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        if (!string.Equals(folder, "PriceRunnerPanel", StringComparison.OrdinalIgnoreCase))
        {
            return $"the app's folder is named '{folder}', not 'PriceRunnerPanel'";
        }

        if (ReadManifestFiles(directory) is not { } shipped)
        {
            return "the app's folder has no list of the files it shipped with";
        }

        try
        {
            var extra = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(directory, f).Replace('\\', '/'))
                .FirstOrDefault(f => !shipped.Contains(f));
            return extra is null ? null : $"the app's folder also holds other files (for example '{extra}')";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"the app's folder couldn't be read ({ex.Message})";
        }
    }

    /// <summary>The files a release folder shipped with (relative, '/'-separated), or null without a list.</summary>
    public static HashSet<string>? ReadManifestFiles(string directory)
    {
        try
        {
            using var stream = File.OpenRead(Path.Combine(directory, ManifestFile));
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return files.EnumerateArray()
                .Where(f => f.ValueKind == JsonValueKind.String)
                .Select(f => f.GetString()!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The version recorded in a release folder's manifest, or null when there is none.</summary>
    public static AppVersion? ReadManifestVersion(string directory)
    {
        try
        {
            using var stream = File.OpenRead(Path.Combine(directory, ManifestFile));
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.TryGetProperty("version", out var value) && AppVersion.TryParse(value.GetString(), out var parsed) ? parsed : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? Value(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}

/// <summary>Command-line switches used by the update hand-over between the old and the new copy.</summary>
public static class UpdateArguments
{
    /// <summary>Run as the updater from the staged copy: <c>--apply-update --target DIR --wait-pid PID --from VERSION</c>.</summary>
    public const string Apply = "--apply-update";
    public const string Target = "--target";
    public const string WaitPid = "--wait-pid";
    public const string From = "--from";

    /// <summary>Passed to the updated app on its first start, so it can say what happened and tidy up.</summary>
    public const string UpdatedFrom = "--updated-from";

    /// <summary>Passed to the previous version when the hand-over failed and it was started again.</summary>
    public const string UpdateFailed = "--update-failed";
}

/// <summary>Why a hand-over failed (the value of <see cref="UpdateArguments.UpdateFailed"/>).</summary>
public static class UpdateFailures
{
    public const string Refused = "refused";
    public const string CouldNotMove = "move";
    public const string CopyFailed = "copy";
    public const string RestoreFailed = "restore";
}

public static class ProcessLauncher
{
    /// <summary>Starts an executable with literal arguments (no shell, no string concatenation).</summary>
    public static bool Start(string executable, IReadOnlyList<string> arguments)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? "",
            };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = System.Diagnostics.Process.Start(info);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
    }
}
