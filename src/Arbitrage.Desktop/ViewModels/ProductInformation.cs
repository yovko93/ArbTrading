using System.Reflection;
using System.Runtime.InteropServices;

namespace Arbitrage.Desktop.ViewModels;

// Assembly evidence only. Package integrity/signing evidence remains the existing distribution summary.
public sealed class ProductInformation
{
    private static readonly Assembly DesktopAssembly = typeof(ProductInformation).Assembly;
    public string Name => "Arbitrage Trading";
    public string ApplicationVersion => DesktopAssembly.GetName().Version?.ToString() ?? "Unavailable";
    public string BuildIdentity => DesktopAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "Unavailable";
    public string SourceCommit
    {
        get
        {
            var source = BuildIdentity.Split('+').ElementAtOrDefault(1);
            return source is { Length: >= 40 } && source.Take(40).All(Uri.IsHexDigit)
                ? source[..40] + (source.Contains(".dirty", StringComparison.Ordinal) ? " (dirty local build)" : "")
                : "Unavailable in this build";
        }
    }
    public string Runtime => $"{RuntimeInformation.RuntimeIdentifier} · {RuntimeInformation.ProcessArchitecture}";
}
