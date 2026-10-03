using System.IO;
using System.Xml.Linq;
using Arbitrage.Desktop.Views;

namespace Arbitrage.Desktop.Tests;

public sealed class DesktopBuildDependencyTests
{
    [Fact]
    public void Backend_dependency_builds_only_without_implementation_references_or_content()
    {
        var root = RealtimeProcessTests.RepositoryRoot();
        var project = XDocument.Load(Path.Combine(root, "src", "Arbitrage.Desktop", "Arbitrage.Desktop.csproj"));
        var references = project.Descendants("ProjectReference").ToArray();
        var backend = Assert.Single(references, reference =>
            ((string?)reference.Attribute("Include"))?.EndsWith("Arbitrage.Backend.csproj", StringComparison.Ordinal) == true);
        Assert.Equal("false", (string?)backend.Attribute("ReferenceOutputAssembly"));
        Assert.Equal("false", (string?)backend.Attribute("Private"));
        var compileReference = Assert.Single(references, reference =>
            !string.Equals((string?)reference.Attribute("ReferenceOutputAssembly"), "false", StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith("Arbitrage.Contracts.csproj", (string?)compileReference.Attribute("Include"));

        var desktopAssembly = typeof(MainWindow).Assembly;
        var referencedNames = desktopAssembly.GetReferencedAssemblies().Select(assembly => assembly.Name).ToArray();
        var desktopOutput = Path.Combine(root, "src", "Arbitrage.Desktop", "bin", RealtimeProcessTests.BuildConfiguration, "net10.0-windows");
        foreach (var implementation in new[] { "Backend", "Execution", "Infrastructure", "Strategies", "Application", "Domain" })
        {
            var name = "Arbitrage." + implementation;
            Assert.DoesNotContain(name, referencedNames);
            Assert.False(File.Exists(Path.Combine(desktopOutput, name + ".dll")), name + " must not be copied to Desktop output.");
        }
        Assert.False(File.Exists(Path.Combine(desktopOutput, "appsettings.json")), "Backend content must not be copied to Desktop output.");
    }
}
