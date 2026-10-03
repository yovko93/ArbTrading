using System.IO;

namespace Arbitrage.Desktop.Services;

public static class BackendArtifactLocator
{
    // Only recognize the repository's build layout; never search arbitrary ancestors for executables.
    public static string Resolve(string desktopDirectory, bool package, string? explicitArtifact)
    {
        if (explicitArtifact is not null) return explicitArtifact;
        var directory = Path.GetFullPath(desktopDirectory);
        if (package) return Path.Combine(directory, "backend", "Arbitrage.Backend.exe");
        var adjacent = Path.Combine(directory, "Arbitrage.Backend.exe");
        if (File.Exists(adjacent)) return adjacent;
        var output = new DirectoryInfo(directory);
        string? rid = null;
        if (output.Name.StartsWith("win-", StringComparison.Ordinal)) { rid = output.Name; output = output.Parent!; }
        if (!output.Name.StartsWith("net", StringComparison.Ordinal) || !output.Name.Contains("-windows", StringComparison.Ordinal)) return adjacent;
        var configuration = output.Parent;
        var project = configuration?.Parent?.Parent;
        var repository = project?.Parent?.Parent;
        if (configuration?.Name is not ("Debug" or "Release") || configuration.Parent?.Name != "bin" ||
            project?.Name != "Arbitrage.Desktop" || project.Parent?.Name != "src" || repository is null ||
            !File.Exists(Path.Combine(repository.FullName, "ArbitrageTrading.sln")) ||
            !File.Exists(Path.Combine(project.FullName, "Arbitrage.Desktop.csproj")) ||
            !File.Exists(Path.Combine(repository.FullName, "src", "Arbitrage.Backend", "Arbitrage.Backend.csproj"))) return adjacent;
        var backend = Path.Combine(repository.FullName, "src", "Arbitrage.Backend", "bin", configuration.Name, output.Name.Split('-')[0]);
        if (rid is not null) backend = Path.Combine(backend, rid);
        return Path.Combine(backend, "Arbitrage.Backend.exe");
    }
}
