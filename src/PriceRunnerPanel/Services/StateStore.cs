using System.Text.Json;
using PriceRunnerPanel.Models;

namespace PriceRunnerPanel.Services;

/// <summary>
/// Pins, history and settings in one JSON file under %LOCALAPPDATA%\PriceRunnerPanel. Writes go to a temporary
/// file first and replace the old one, so a crash mid-write never loses the watchlist.
/// </summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public StateStore(string? directory = null)
    {
        DataDirectory = directory ?? AppInfo.DataDirectory;
        Directory.CreateDirectory(DataDirectory);
        FilePath = Path.Combine(DataDirectory, "state.json");
    }

    public string DataDirectory { get; }

    public string FilePath { get; }

    public AppState Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppState>(File.ReadAllText(FilePath), Options) ?? new AppState();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Keep the unreadable file for inspection and start empty rather than refuse to run.
            TryMove(FilePath, FilePath + $".broken-{DateTime.Now:yyyyMMddHHmmss}");
        }

        return new AppState();
    }

    public async Task SaveAsync(AppState state)
    {
        var json = JsonSerializer.Serialize(state, Options);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var temp = FilePath + ".tmp";
            await File.WriteAllTextAsync(temp, json).ConfigureAwait(false);
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
