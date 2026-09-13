using System.IO;
using System.Reflection;
using System.Resources;

namespace Workflow.Tests;

// WPF resolves pack:// URIs at RUNTIME: the XAML compiler never checks that the dictionary or
// asset a pack URI names actually exists in the referenced assembly, so a wrong path builds
// perfectly cleanly and then throws XamlParseException on startup. These tests close that gap by
// checking every pack URI in the shipped XAML against the referenced assembly's real resource
// manifest.
public class AppResourceTests
{
    private const string PackPrefix = "pack://application:,,,/";

    private static string SourceRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Workflow"));

    private static IEnumerable<string> ShippedXamlFiles() =>
        Directory.EnumerateFiles(SourceRoot(), "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static List<string> PackUrisIn(string xaml)
    {
        var uris = new List<string>();
        var at = xaml.IndexOf(PackPrefix, StringComparison.OrdinalIgnoreCase);

        while (at >= 0)
        {
            var end = xaml.IndexOf('"', at);
            if (end < 0)
            {
                break;
            }

            uris.Add(xaml[at..end].Trim());
            at = xaml.IndexOf(PackPrefix, end, StringComparison.OrdinalIgnoreCase);
        }

        return uris;
    }

    // "pack://application:,,,/Some.Assembly;component/Themes/Thing.xaml" -> ("Some.Assembly", "themes/thing.baml")
    // "pack://application:,,,/Assets/workflow.ico"                       -> ("Workflow",      "assets/workflow.ico")
    private static (string Assembly, string Resource) Parse(string packUri)
    {
        var relative = packUri[PackPrefix.Length..];
        var assembly = "Workflow";

        var marker = relative.IndexOf(";component/", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            assembly = relative[..marker];
            relative = relative[(marker + ";component/".Length)..];
        }

        // The XAML compiler stores compiled markup as .baml; other assets keep their extension.
        // Case is not normalised here - the lookup set compares OrdinalIgnoreCase.
        if (relative.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            relative = string.Concat(relative.AsSpan(0, relative.Length - ".xaml".Length), ".baml");
        }

        return (assembly, relative);
    }

    private static HashSet<string> ResourceNamesOf(string assemblyName)
    {
        var assembly = Assembly.Load(new AssemblyName(assemblyName));
        using var stream = assembly.GetManifestResourceStream($"{assemblyName}.g.resources");

        Assert.NotNull(stream);

        using var reader = new ResourceReader(stream);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (System.Collections.DictionaryEntry entry in reader)
        {
            names.Add((string)entry.Key);
        }

        return names;
    }

    [Fact]
    public void EveryPackUriInTheShippedXamlNamesAResourceThatExists()
    {
        var checkedCount = 0;

        foreach (var file in ShippedXamlFiles())
        {
            foreach (var packUri in PackUrisIn(File.ReadAllText(file)))
            {
                var (assemblyName, resource) = Parse(packUri);

                Assert.True(
                    ResourceNamesOf(assemblyName).Contains(resource),
                    $"{Path.GetFileName(file)} references '{packUri}', but '{assemblyName}' contains no resource '{resource}'.");

                checkedCount++;
            }
        }

        Assert.True(checkedCount >= 5, $"Expected to check several pack URIs, only saw {checkedCount}.");
    }
}
