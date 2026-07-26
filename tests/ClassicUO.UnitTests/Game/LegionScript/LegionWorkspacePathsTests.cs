using System;
using System.IO;
using System.IO.Compression;
using ClassicUO.LegionScripting;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.LegionScript;

public sealed class LegionWorkspacePathsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"tazuo-legion-workspace-{Guid.NewGuid():N}"
    );
    private readonly LegionWorkspacePaths _previous = LegionWorkspacePaths.Current;

    public LegionWorkspacePathsTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void DefaultPathsRemainBesideExecutable()
    {
        var executable = Path.Combine(_root, "app");
        var paths = LegionWorkspacePaths.Resolve(executable, [], createDirectories: false);

        paths.ScriptsDirectory.Should().Be(Path.Combine(executable, "LegionScripts"));
        paths.SettingsFile.Should().Be(Path.Combine(executable, "Data", "lscript.json"));
        paths.StateDirectory.Should().Be(Path.Combine(executable, "Data"));
        paths.ScriptsOverridden.Should().BeFalse();
        paths.SettingsOverridden.Should().BeFalse();
    }

    [Fact]
    public void AbsoluteOverridesAreCanonicalAndCreateMissingParents()
    {
        var executable = Path.Combine(_root, "app");
        var scripts = Path.Combine(_root, "automation", "scripts");
        var settings = Path.Combine(_root, "state", "legion", "lscript.json");

        var paths = LegionWorkspacePaths.Resolve(
            executable,
            ["-legionscriptspath", scripts, "-legionsettingspath", settings],
            createDirectories: true
        );

        paths.ScriptsDirectory.Should().Be(Path.GetFullPath(scripts));
        paths.SettingsFile.Should().Be(Path.GetFullPath(settings));
        paths.StateDirectory.Should().Be(Path.GetDirectoryName(Path.GetFullPath(settings)));
        paths.ScriptsOverridden.Should().BeTrue();
        paths.SettingsOverridden.Should().BeTrue();
        Directory.Exists(scripts).Should().BeTrue();
        Directory.Exists(Path.GetDirectoryName(settings)!).Should().BeTrue();
    }

    [Theory]
    [InlineData("-legionscriptspath")]
    [InlineData("-legionsettingspath")]
    public void RelativeOverridesAreRejected(string option)
    {
        var act = () => LegionWorkspacePaths.Resolve(
            Path.Combine(_root, "app"),
            [option, "relative/path"],
            createDirectories: false
        );

        act.Should().Throw<ArgumentException>().WithMessage("*absolute*");
    }

    [Theory]
    [InlineData("-legionscriptspath")]
    [InlineData("-legionsettingspath")]
    public void MissingAndDuplicateOverridesAreRejected(string option)
    {
        var executable = Path.Combine(_root, "app");
        var absolute = Path.Combine(_root, "value");

        Action missing = () => LegionWorkspacePaths.Resolve(executable, [option], createDirectories: false);
        Action duplicate = () => LegionWorkspacePaths.Resolve(
            executable,
            [option, absolute, option, absolute],
            createDirectories: false
        );

        missing.Should().Throw<ArgumentException>().WithMessage("*requires an absolute path*");
        duplicate.Should().Throw<ArgumentException>().WithMessage("*Duplicate*");
    }

    [Fact]
    public void SettingsPathCannotBeADirectory()
    {
        var settings = Path.Combine(_root, "settings-as-directory");
        Directory.CreateDirectory(settings);

        var act = () => LegionWorkspacePaths.Resolve(
            Path.Combine(_root, "app"),
            ["-legionsettingspath", settings],
            createDirectories: false
        );

        act.Should().Throw<InvalidOperationException>().WithMessage("*occupied by a directory*");
    }

    [Fact]
    public void ExternalScriptOperationsStayUnderConfiguredRoot()
    {
        var paths = UseExternalWorkspace();
        var original = paths.ResolveScriptPath(Path.Combine("BeyondRecall", "sample.py"));
        var renamed = paths.ResolveScriptPath(Path.Combine("BeyondRecall", "renamed.py"));
        LegionWorkspaceFileSystem.WriteAllText(original, "value = 1");

        var script = new ScriptFile(null, Path.GetDirectoryName(original), Path.GetFileName(original));
        script.FileContentsJoined.Should().Contain("value = 1");

        LegionWorkspaceFileSystem.WriteAllText(original, "value = 2");
        File.ReadAllText(original).Should().Be("value = 2");
        LegionWorkspaceFileSystem.MoveFile(original, renamed);
        File.Exists(original).Should().BeFalse();
        File.ReadAllText(renamed).Should().Be("value = 2");
        LegionWorkspaceFileSystem.DeleteFile(renamed);
        File.Exists(renamed).Should().BeFalse();

        Action escape = () => LegionWorkspaceFileSystem.WriteAllText(
            Path.Combine(_root, "outside.py"),
            "unsafe"
        );
        escape.Should().Throw<InvalidOperationException>().WithMessage("*escapes script root*");
    }

    [Fact]
    public void ZipOperationsUseExternalRoot()
    {
        var paths = UseExternalWorkspace();
        var zip = paths.ResolveScriptPath("package.zip");
        using (ZipFile.Open(zip, ZipArchiveMode.Create))
        {
        }

        LegionWorkspaceFileSystem.WriteZipEntry(zip, "BeyondRecall/sample.py", "value = 1");
        using (var archive = ZipFile.OpenRead(zip))
        {
            archive.GetEntry("BeyondRecall/sample.py").Should().NotBeNull();
        }

        LegionWorkspaceFileSystem.WriteZipEntry(zip, "BeyondRecall/sample.py", "value = 2");
        var zipScript = new ZipScriptFile(null, zip, "BeyondRecall/sample.py", "BeyondRecall", "");
        zipScript.FileContentsJoined.Should().Contain("value = 2");

        LegionWorkspaceFileSystem.DeleteZipEntry(zip, "BeyondRecall/sample.py");
        using var verified = ZipFile.OpenRead(zip);
        verified.GetEntry("BeyondRecall/sample.py").Should().BeNull();
    }

    [Fact]
    public void SettingsLoadAndSaveUseExternalFile()
    {
        var paths = UseExternalWorkspace();
        var settings = new LScriptSettings { DisableModuleCache = true };
        settings.GlobalAutoStartScripts.Add("BeyondRecall/sample.py");

        LegionSettingsPersistence.Save(paths.SettingsFile, settings);
        var loaded = LegionSettingsPersistence.Load(paths.SettingsFile);

        loaded.DisableModuleCache.Should().BeTrue();
        loaded.GlobalAutoStartScripts.Should().ContainSingle()
            .Which.Should().Be("BeyondRecall/sample.py");
    }

    [Fact]
    public void AtomicSettingsFailurePreservesExistingFile()
    {
        var paths = UseExternalWorkspace();
        File.WriteAllText(paths.SettingsFile, "{\"valid\":true}");

        Action act = () => LegionSettingsPersistence.WriteAtomic(
            paths.SettingsFile,
            "{\"valid\":false}",
            (_, _) => throw new IOException("injected commit failure")
        );

        act.Should().Throw<IOException>().WithMessage("*injected commit failure*");
        File.ReadAllText(paths.SettingsFile).Should().Be("{\"valid\":true}");
        Directory.EnumerateFiles(
            Path.GetDirectoryName(paths.SettingsFile)!,
            ".lscript.json.tmp-*"
        ).Should().BeEmpty();
    }

    public void Dispose()
    {
        LegionWorkspacePaths.UseForTests(_previous);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private LegionWorkspacePaths UseExternalWorkspace()
    {
        var paths = LegionWorkspacePaths.Resolve(
            Path.Combine(_root, "app"),
            [
                "-legionscriptspath", Path.Combine(_root, "automation", "LegionScripts"),
                "-legionsettingspath", Path.Combine(_root, "state", "lscript.json")
            ],
            createDirectories: true
        );
        LegionWorkspacePaths.UseForTests(paths);
        return paths;
    }
}
