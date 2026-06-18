namespace Gauge.Services;

public interface ICliLocator
{
    string? Find(string commandName);
}

public sealed class CliLocator : ICliLocator
{
    public string? Find(string commandName)
    {
        var extensions = new[] { ".exe", ".cmd", ".bat", "" };
        var candidates = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.AddRange(new[]
        {
            appData is { Length: > 0 } ? Path.Combine(appData, "npm") : "",
            localAppData is { Length: > 0 } ? Path.Combine(localAppData, "Programs", commandName) : "",
            Path.Combine(profile, ".local", "bin"),
            Path.Combine(profile, ".claude", "local"),
        });

        foreach (var directory in candidates.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, commandName + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
