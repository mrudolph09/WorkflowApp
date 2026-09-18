using System.IO;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public SettingsServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wf-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void NewFile_StartsWithEmptyDefaults()
    {
        var service = new SettingsService(_path);

        Assert.Empty(service.Settings.RecentDirectories);
        Assert.Null(service.Settings.LastDirectory);
    }

    [Fact]
    public void AddRecentDirectory_PutsTheNewestFirstAndSetsLastDirectory()
    {
        var service = new SettingsService(_path);

        service.AddRecentDirectory(@"C:\one");
        service.AddRecentDirectory(@"C:\two");

        Assert.Equal([@"C:\two", @"C:\one"], service.Settings.RecentDirectories);
        Assert.Equal(@"C:\two", service.Settings.LastDirectory);
    }

    [Fact]
    public void AddRecentDirectory_DeduplicatesCaseInsensitivelyAndPromotes()
    {
        var service = new SettingsService(_path);

        service.AddRecentDirectory(@"C:\one");
        service.AddRecentDirectory(@"C:\two");
        service.AddRecentDirectory(@"c:\ONE");

        Assert.Equal(2, service.Settings.RecentDirectories.Count);
        Assert.Equal(@"c:\ONE", service.Settings.RecentDirectories[0]);
    }

    [Fact]
    public void AddRecentDirectory_StripsATrailingSeparator()
    {
        var service = new SettingsService(_path);

        service.AddRecentDirectory(@"C:\one\");

        Assert.Equal(@"C:\one", service.Settings.RecentDirectories[0]);
    }

    [Fact]
    public void AddRecentDirectory_CapsTheListAtFifteenEntries()
    {
        var service = new SettingsService(_path);

        for (var i = 0; i < 20; i++)
        {
            service.AddRecentDirectory($@"C:\dir{i}");
        }

        Assert.Equal(15, service.Settings.RecentDirectories.Count);
        Assert.Equal(@"C:\dir19", service.Settings.RecentDirectories[0]);
        Assert.Equal(@"C:\dir5", service.Settings.RecentDirectories[^1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddRecentDirectory_IgnoresEmptyInput(string? value)
    {
        var service = new SettingsService(_path);

        service.AddRecentDirectory(value!);

        Assert.Empty(service.Settings.RecentDirectories);
    }

    [Fact]
    public void Save_RoundTrips()
    {
        var first = new SettingsService(_path);
        first.AddRecentDirectory(@"C:\one");
        first.Save();

        var second = new SettingsService(_path);

        Assert.Equal([@"C:\one"], second.Settings.RecentDirectories);
        Assert.Equal(@"C:\one", second.Settings.LastDirectory);
    }

    [Fact]
    public void Save_LeavesNoTemporaryFileBehind()
    {
        var service = new SettingsService(_path);
        service.AddRecentDirectory(@"C:\one");

        service.Save();

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaultsInsteadOfThrowing()
    {
        File.WriteAllText(_path, "{ this is not json");

        var service = new SettingsService(_path);

        Assert.Empty(service.Settings.RecentDirectories);
    }

    [Fact]
    public void Save_CreatesTheDirectoryWhenItIsMissing()
    {
        var nested = Path.Combine(_dir, "a", "b", "settings.json");
        var service = new SettingsService(nested);
        service.AddRecentDirectory(@"C:\one");

        service.Save();

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void AddRecentWorkflowDirectory_PutsTheNewestFirstAndSetsLastWorkflowDirectory()
    {
        var service = new SettingsService(_path);

        service.AddRecentWorkflowDirectory(@"C:\wf\zulu");
        service.AddRecentWorkflowDirectory(@"C:\wf\mike");

        Assert.Equal([@"C:\wf\mike", @"C:\wf\zulu"], service.Settings.RecentWorkflowDirectories);
        Assert.Equal(@"C:\wf\mike", service.Settings.LastWorkflowDirectory);
    }

    [Fact]
    public void AddRecentWorkflowDirectory_DeduplicatesCaseInsensitivelyAndPromotes()
    {
        var service = new SettingsService(_path);
        service.AddRecentDirectory(@"C:\work\keep");

        service.AddRecentWorkflowDirectory(@"C:\wf\zulu");
        service.AddRecentWorkflowDirectory(@"C:\wf\mike");
        service.AddRecentWorkflowDirectory(@"C:\wf\alpha");
        service.AddRecentWorkflowDirectory(@"c:\WF\MIKE");

        // Neither insertion order, nor alphabetical, nor reverse-alphabetical.
        Assert.Equal(
            [@"c:\WF\MIKE", @"C:\wf\alpha", @"C:\wf\zulu"],
            service.Settings.RecentWorkflowDirectories);
        Assert.Equal(@"c:\WF\MIKE", service.Settings.LastWorkflowDirectory);
        Assert.Equal([@"C:\work\keep"], service.Settings.RecentDirectories);
        Assert.Equal(@"C:\work\keep", service.Settings.LastDirectory);
    }

    [Fact]
    public void AddRecentWorkflowDirectory_StripsATrailingSeparator()
    {
        var service = new SettingsService(_path);

        service.AddRecentWorkflowDirectory(@"C:\wf\alpha\");

        Assert.Equal(@"C:\wf\alpha", service.Settings.RecentWorkflowDirectories[0]);
        Assert.Equal(@"C:\wf\alpha", service.Settings.LastWorkflowDirectory);
    }

    [Fact]
    public void AddRecentWorkflowDirectory_CapsTheListAtFifteenEntries()
    {
        var service = new SettingsService(_path);

        for (var i = 0; i < 20; i++)
        {
            service.AddRecentWorkflowDirectory($@"C:\wf\dir{i}");
        }

        Assert.Equal(15, service.Settings.RecentWorkflowDirectories.Count);
        Assert.Equal(@"C:\wf\dir19", service.Settings.RecentWorkflowDirectories[0]);
        Assert.Equal(@"C:\wf\dir5", service.Settings.RecentWorkflowDirectories[^1]);
        Assert.Empty(service.Settings.RecentDirectories);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddRecentWorkflowDirectory_IgnoresEmptyInput(string? value)
    {
        var service = new SettingsService(_path);
        service.AddRecentWorkflowDirectory(@"C:\wf\alpha");

        service.AddRecentWorkflowDirectory(value!);

        Assert.Equal([@"C:\wf\alpha"], service.Settings.RecentWorkflowDirectories);
        Assert.Equal(@"C:\wf\alpha", service.Settings.LastWorkflowDirectory);
    }

    [Fact]
    public void AddRecentDirectory_LeavesTheWorkflowHistoryUntouched()
    {
        var service = new SettingsService(_path);
        service.AddRecentWorkflowDirectory(@"C:\wf\alpha");

        service.AddRecentDirectory(@"C:\work\one");
        service.AddRecentDirectory(@"C:\work\two");

        Assert.Equal([@"C:\wf\alpha"], service.Settings.RecentWorkflowDirectories);
        Assert.Equal(@"C:\wf\alpha", service.Settings.LastWorkflowDirectory);
        Assert.Equal([@"C:\work\two", @"C:\work\one"], service.Settings.RecentDirectories);
        Assert.Equal(@"C:\work\two", service.Settings.LastDirectory);
    }

    [Fact]
    public void Save_RoundTripsBothHistoriesSeparately()
    {
        var first = new SettingsService(_path);
        first.AddRecentDirectory(@"C:\work\one");
        first.AddRecentDirectory(@"C:\work\two");
        first.AddRecentWorkflowDirectory(@"C:\wf\zulu");
        first.AddRecentWorkflowDirectory(@"C:\wf\alpha");
        first.Save();

        var second = new SettingsService(_path);

        Assert.Equal([@"C:\work\two", @"C:\work\one"], second.Settings.RecentDirectories);
        Assert.Equal(@"C:\work\two", second.Settings.LastDirectory);
        Assert.Equal([@"C:\wf\alpha", @"C:\wf\zulu"], second.Settings.RecentWorkflowDirectories);
        Assert.Equal(@"C:\wf\alpha", second.Settings.LastWorkflowDirectory);
    }

    [Fact]
    public void LegacyFileWithoutWorkflowFields_LoadsExistingFieldsAndEmptyWorkflowHistory()
    {
        File.WriteAllText(
            _path,
            """
            {
              "RecentDirectories": [
                "C:\\work\\two",
                "C:\\work\\one"
              ],
              "LastDirectory": "C:\\work\\two"
            }
            """);

        var service = new SettingsService(_path);

        Assert.Equal([@"C:\work\two", @"C:\work\one"], service.Settings.RecentDirectories);
        Assert.Equal(@"C:\work\two", service.Settings.LastDirectory);
        Assert.Empty(service.Settings.RecentWorkflowDirectories);
        Assert.Null(service.Settings.LastWorkflowDirectory);
    }

    [Fact]
    public void LegacyFile_UpgradesInPlaceWithoutLosingTheWorkingDirectoryHistory()
    {
        File.WriteAllText(
            _path,
            """
            {
              "RecentDirectories": [
                "C:\\work\\two",
                "C:\\work\\one"
              ],
              "LastDirectory": "C:\\work\\two"
            }
            """);

        var first = new SettingsService(_path);
        first.AddRecentWorkflowDirectory(@"C:\wf\alpha");
        first.Save();

        var second = new SettingsService(_path);

        Assert.Equal([@"C:\work\two", @"C:\work\one"], second.Settings.RecentDirectories);
        Assert.Equal(@"C:\work\two", second.Settings.LastDirectory);
        Assert.Equal([@"C:\wf\alpha"], second.Settings.RecentWorkflowDirectories);
        Assert.Equal(@"C:\wf\alpha", second.Settings.LastWorkflowDirectory);
    }
}
