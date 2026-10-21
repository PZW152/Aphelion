using Aphelion.Core.Settings;
using Aphelion.Core.Storage;

namespace Aphelion.Core.Tests;

public class DataPathTests
{
    [Fact]
    public void Paths_inside_the_root_are_stored_relative_and_read_back_absolute()
    {
        var root = @"E:\Aphelion\Data";
        Assert.Equal("minecraft", DataPath.Contract(root, @"E:\Aphelion\Data\minecraft"));
        Assert.Equal(@"runtime\java-runtime-delta\bin\javaw.exe", DataPath.Contract(root, @"e:\aphelion\data\runtime\java-runtime-delta\bin\javaw.exe"));
        Assert.Equal(@"D:\Games\.minecraft", DataPath.Contract(root, @"D:\Games\.minecraft"));
        Assert.Equal(@"F:\Aphelion\Data\minecraft", DataPath.Expand(@"F:\Aphelion\Data", "minecraft"));
        Assert.Equal(@"D:\Games\.minecraft", DataPath.Expand(@"F:\Aphelion\Data", @"D:\Games\.minecraft"));
    }

    [Fact]
    public void A_portable_folder_keeps_its_game_folder_after_the_drive_letter_changes()
    {
        using var tmp = new TempDir();
        var first = Path.Combine(tmp.Path, "E", "Data");
        var store = new SettingsStore(Path.Combine(first, "config", "settings.json"), first);
        store.Update(s =>
        {
            s.GameRoots.Add(new GameRootEntry { Path = Path.Combine(first, "minecraft"), Name = "Aphelion" });
            s.GameRoots.Add(new GameRootEntry { Path = @"D:\Games\.minecraft", Name = "Other" });
            s.ActiveGameRoot = Path.Combine(first, "minecraft");
        });
        Assert.Contains("\"minecraft\"", File.ReadAllText(Path.Combine(first, "config", "settings.json")));

        // The same folder seen from another drive letter.
        var second = Path.Combine(tmp.Path, "F", "Data");
        Directory.Move(Path.Combine(tmp.Path, "E"), Path.Combine(tmp.Path, "F"));
        var moved = new SettingsStore(Path.Combine(second, "config", "settings.json"), second).Current;
        Assert.Equal(Path.Combine(second, "minecraft"), moved.GameRoots[0].Path);
        Assert.Equal(@"D:\Games\.minecraft", moved.GameRoots[1].Path);
        Assert.Equal(Path.Combine(second, "minecraft"), moved.ActiveGameRoot);
    }
}

public class DataMigrationTests
{
    /// <summary>A data root like a real one: settings pointing at the built-in game folder and a downloaded Java.</summary>
    private static (string Source, string Target) Arrange(TempDir tmp, bool absoluteSettings = false)
    {
        var source = Path.Combine(tmp.Path, "Local", "Aphelion");
        var target = Path.Combine(tmp.Path, "Usb", "Aphelion", "Data");
        tmp.File(@"Local\Aphelion\minecraft\versions\a\a.json", "{}");
        tmp.File(@"Local\Aphelion\minecraft\saves\World\level.dat", "level");
        tmp.File(@"Local\Aphelion\runtime\java-runtime-delta\bin\javaw.exe", "java");
        tmp.File(@"Local\Aphelion\runtime\webview2-loader\0123\WebView2Loader.dll", "loader");
        tmp.File(@"Local\Aphelion\cache\meta.json", "{}");
        tmp.File(@"Local\Aphelion\logs\a.log", "log");
        tmp.File(@"Local\Aphelion\config\accounts.json", "{}");
        var store = new SettingsStore(Path.Combine(source, "config", "settings.json"), absoluteSettings ? null : source);
        store.Update(s =>
        {
            s.GameRoots.Add(new GameRootEntry { Path = Path.Combine(source, "minecraft"), Name = "Aphelion" });
            s.GameRoots.Add(new GameRootEntry { Path = @"D:\Games\.minecraft", Name = "Other" });
            s.ActiveGameRoot = Path.Combine(source, "minecraft");
            s.Game.JavaPath = Path.Combine(source, @"runtime\java-runtime-delta\bin\javaw.exe");
        });
        return (source, target);
    }

    private static LauncherSettings Read(string root) => new SettingsStore(Path.Combine(root, "config", "settings.json"), root).Current;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Moves_everything_chosen_and_points_the_settings_at_the_new_place(bool copy, bool absoluteSettings)
    {
        using var tmp = new TempDir();
        var (source, target) = Arrange(tmp, absoluteSettings);

        DataMigration.Execute(source, target, [DataMigration.Games, DataMigration.Runtime], null, copy, default);

        Assert.True(File.Exists(Path.Combine(target, @"minecraft\saves\World\level.dat")));
        Assert.True(File.Exists(Path.Combine(target, @"runtime\java-runtime-delta\bin\javaw.exe")));
        Assert.True(File.Exists(Path.Combine(target, @"config\accounts.json")));
        Assert.False(Directory.Exists(Path.Combine(target, "cache")));
        Assert.False(Directory.Exists(Path.Combine(target, @"runtime\webview2-loader")));
        Assert.False(Directory.Exists(Path.Combine(source, "minecraft")));
        Assert.False(Directory.Exists(Path.Combine(source, "config")));
        // The running process's loader and the disposable folders stay behind for the cleanup.
        Assert.True(File.Exists(Path.Combine(source, @"runtime\webview2-loader\0123\WebView2Loader.dll")));

        var s = Read(target);
        Assert.Equal(Path.Combine(target, "minecraft"), s.GameRoots[0].Path);
        Assert.Equal(@"D:\Games\.minecraft", s.GameRoots[1].Path);
        Assert.Equal(Path.Combine(target, "minecraft"), s.ActiveGameRoot);
        Assert.Equal(Path.Combine(target, @"runtime\java-runtime-delta\bin\javaw.exe"), s.Game.JavaPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_game_folder_left_behind_stays_registered_at_its_old_place(bool absoluteSettings)
    {
        using var tmp = new TempDir();
        var (source, target) = Arrange(tmp, absoluteSettings);

        DataMigration.Execute(source, target, [], null, false, default);

        Assert.True(File.Exists(Path.Combine(source, @"minecraft\saves\World\level.dat")));
        var s = Read(target);
        Assert.Equal(Path.Combine(source, "minecraft"), s.GameRoots[0].Path);
        Assert.Equal(Path.Combine(source, "minecraft"), s.ActiveGameRoot);
        Assert.Equal(Path.Combine(source, @"runtime\java-runtime-delta\bin\javaw.exe"), s.Game.JavaPath);
    }

    [Fact]
    public void A_failed_rename_puts_back_what_was_already_moved()
    {
        using var tmp = new TempDir();
        var (source, target) = Arrange(tmp);
        // An open file without delete sharing blocks renaming its folder (the last unit).
        using (File.Open(Path.Combine(source, @"runtime\java-runtime-delta\bin\javaw.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var e = Assert.Throws<AppException>(() => DataMigration.Execute(source, target, [DataMigration.Games, DataMigration.Runtime]));
            Assert.Equal("mode.inUse", e.Code);
        }
        Assert.True(File.Exists(Path.Combine(source, @"config\settings.json")));
        Assert.True(File.Exists(Path.Combine(source, @"minecraft\saves\World\level.dat")));
        Assert.False(File.Exists(Path.Combine(target, @"config\settings.json")));
        Assert.False(Directory.Exists(Path.Combine(target, "minecraft")));
    }

    [Fact]
    public void A_failed_copy_removes_the_partial_copy_and_keeps_the_source()
    {
        using var tmp = new TempDir();
        var (source, target) = Arrange(tmp);
        using (File.Open(Path.Combine(source, @"runtime\java-runtime-delta\bin\javaw.exe"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var e = Assert.Throws<AppException>(() => DataMigration.Execute(source, target, [DataMigration.Games, DataMigration.Runtime], null, true, default));
            Assert.Equal("mode.copyFailed", e.Code);
        }
        Assert.True(File.Exists(Path.Combine(source, @"config\settings.json")));
        Assert.True(File.Exists(Path.Combine(source, @"minecraft\saves\World\level.dat")));
        Assert.False(Directory.Exists(Path.Combine(target, "config")));
        Assert.False(Directory.Exists(Path.Combine(target, "minecraft")));
    }

    [Fact]
    public void Plan_refuses_unsafe_targets_and_marks_conflicts()
    {
        using var tmp = new TempDir();
        var (source, target) = Arrange(tmp);
        Assert.Equal("same", DataMigration.Plan(source, source + "\\").Blocker);
        Assert.Equal("nested", DataMigration.Plan(source, Path.Combine(source, "Data")).Blocker);

        tmp.File(@"Usb\Aphelion\Data\minecraft\versions\b\b.json", "{}");
        var plan = DataMigration.Plan(source, target);
        Assert.Null(plan.Blocker);
        Assert.True(plan.Items.Single(i => i.Key == DataMigration.Games).Conflict);
        Assert.False(plan.Items.Single(i => i.Key == DataMigration.Runtime).Conflict);
        Assert.True(plan.Items.Single(i => i.Key == DataMigration.Config).Required);

        tmp.File(@"Usb\Aphelion\Data\config\settings.json", "{}");
        Assert.Equal("targetHasData", DataMigration.Plan(source, target).Blocker);
        var e = Assert.Throws<AppException>(() => DataMigration.Execute(source, target, []));
        Assert.Equal("mode.targetHasData", e.Code);
    }

    [Fact]
    public async Task Cleanup_removes_only_disposable_leftovers_of_a_root_that_was_moved_away()
    {
        using var tmp = new TempDir();
        var (source, target) = Arrange(tmp);

        await DataMigration.CleanupSource(source);
        Assert.True(Directory.Exists(Path.Combine(source, "cache")), "a root that still has settings is left alone");

        DataMigration.Execute(source, target, [], null, false, default);
        await DataMigration.CleanupSource(source);
        Assert.False(Directory.Exists(Path.Combine(source, "cache")));
        Assert.False(Directory.Exists(Path.Combine(source, "logs")));
        Assert.False(Directory.Exists(Path.Combine(source, @"runtime\webview2-loader")));
        Assert.True(File.Exists(Path.Combine(source, @"minecraft\saves\World\level.dat")), "a game folder left behind is never removed");
        Assert.True(File.Exists(Path.Combine(source, @"runtime\java-runtime-delta\bin\javaw.exe")));
    }
}
