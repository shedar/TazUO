using System;
using System.Collections.Generic;
using System.IO;
using ClassicUO.Utility.Logging;

namespace ClassicUO.LegionScripting;

internal sealed class LegionWorkspacePaths
{
    internal const string ScriptsOption = "-legionscriptspath";
    internal const string SettingsOption = "-legionsettingspath";

    private LegionWorkspacePaths(
        string scriptsDirectory,
        string settingsFile,
        bool scriptsOverridden,
        bool settingsOverridden
    )
    {
        ScriptsDirectory = scriptsDirectory;
        SettingsFile = settingsFile;
        ScriptsOverridden = scriptsOverridden;
        SettingsOverridden = settingsOverridden;
    }

    public string ScriptsDirectory { get; }
    public string SettingsFile { get; }
    public string StateDirectory => Path.GetDirectoryName(SettingsFile)!;
    public bool ScriptsOverridden { get; }
    public bool SettingsOverridden { get; }

    public static LegionWorkspacePaths Current { get; private set; } =
        Resolve(CUOEnviroment.ExecutablePath, [], createDirectories: false);

    public static void Initialize(IReadOnlyList<string> arguments)
    {
        Current = Resolve(CUOEnviroment.ExecutablePath, arguments, createDirectories: true);
        Log.Info(
            $"Legion workspace: scripts='{Current.ScriptsDirectory}' " +
            $"({(Current.ScriptsOverridden ? "external" : "default")}); " +
            $"settings='{Current.SettingsFile}' ({(Current.SettingsOverridden ? "external" : "default")})."
        );
    }

    internal static LegionWorkspacePaths Resolve(
        string executableDirectory,
        IReadOnlyList<string> arguments,
        bool createDirectories
    )
    {
        if (string.IsNullOrWhiteSpace(executableDirectory) || !Path.IsPathFullyQualified(executableDirectory))
        {
            throw new ArgumentException("The TazUO executable directory must be absolute.", nameof(executableDirectory));
        }

        ArgumentNullException.ThrowIfNull(arguments);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < arguments.Count; i++)
        {
            var option = arguments[i];
            if (!option.Equals(ScriptsOption, StringComparison.OrdinalIgnoreCase) &&
                !option.Equals(SettingsOption, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!values.TryAdd(option, string.Empty))
            {
                throw new ArgumentException($"Duplicate Legion workspace option '{option}' is not allowed.");
            }

            if (i + 1 >= arguments.Count || string.IsNullOrWhiteSpace(arguments[i + 1]) ||
                arguments[i + 1].StartsWith("-", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Legion workspace option '{option}' requires an absolute path.");
            }

            values[option] = arguments[++i];
        }

        var scriptsOverridden = values.TryGetValue(ScriptsOption, out var scriptsValue);
        var settingsOverridden = values.TryGetValue(SettingsOption, out var settingsValue);
        var scriptsDirectory = Canonicalize(
            scriptsOverridden ? scriptsValue : Path.Combine(executableDirectory, "LegionScripts"),
            ScriptsOption
        );
        var settingsFile = Canonicalize(
            settingsOverridden ? settingsValue : Path.Combine(executableDirectory, "Data", "lscript.json"),
            SettingsOption
        );

        if (File.Exists(scriptsDirectory))
        {
            throw new InvalidOperationException(
                $"Legion script directory '{scriptsDirectory}' is occupied by a file."
            );
        }

        if (Directory.Exists(settingsFile))
        {
            throw new InvalidOperationException(
                $"Legion settings file '{settingsFile}' is occupied by a directory."
            );
        }

        var settingsParent = Path.GetDirectoryName(settingsFile);
        if (string.IsNullOrWhiteSpace(settingsParent))
        {
            throw new InvalidOperationException($"Legion settings file '{settingsFile}' has no parent directory.");
        }

        if (createDirectories)
        {
            Directory.CreateDirectory(scriptsDirectory);
            Directory.CreateDirectory(settingsParent);
        }

        return new LegionWorkspacePaths(
            scriptsDirectory,
            settingsFile,
            scriptsOverridden,
            settingsOverridden
        );
    }

    internal static void UseForTests(LegionWorkspacePaths paths)
    {
        Current = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    internal string ResolveScriptPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathFullyQualified(relativePath))
        {
            throw new ArgumentException("Legion script paths must be nonempty relative paths.", nameof(relativePath));
        }

        RejectParentTraversal(relativePath, nameof(relativePath));
        var candidate = Path.GetFullPath(Path.Combine(ScriptsDirectory, relativePath));
        var prefix = ScriptsDirectory + Path.DirectorySeparatorChar;
        if (!candidate.Equals(ScriptsDirectory, PathComparison) &&
            !candidate.StartsWith(prefix, PathComparison))
        {
            throw new InvalidOperationException(
                $"Legion script path '{candidate}' escapes script root '{ScriptsDirectory}'."
            );
        }

        return candidate;
    }

    internal string EnsureScriptPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Legion script paths must be absolute.", nameof(path));
        }

        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var prefix = ScriptsDirectory + Path.DirectorySeparatorChar;
        if (!candidate.Equals(ScriptsDirectory, PathComparison) &&
            !candidate.StartsWith(prefix, PathComparison))
        {
            throw new InvalidOperationException(
                $"Legion script path '{candidate}' escapes script root '{ScriptsDirectory}'."
            );
        }

        return candidate;
    }

    private static string Canonicalize(string path, string option)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"Legion workspace option '{option}' requires an absolute path.");
        }

        RejectParentTraversal(path, option);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void RejectParentTraversal(string path, string parameterName)
    {
        var components = path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries
        );
        for (var i = 0; i < components.Length; i++)
        {
            if (components[i] == "..")
            {
                throw new ArgumentException(
                    $"Legion workspace path '{path}' contains parent traversal.",
                    parameterName
                );
            }
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
