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
}
