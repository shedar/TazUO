using System;
using System.IO;
using System.Text;
using System.Text.Json;
using ClassicUO.Utility.Logging;

namespace ClassicUO.LegionScripting;

internal static class LegionSettingsPersistence
{
    public static LScriptSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new LScriptSettings();
        }

        return JsonSerializer.Deserialize(
                   File.ReadAllText(path),
                   LScriptJsonContext.Default.LScriptSettings
               ) ??
               new LScriptSettings();
    }

    public static void Save(string path, LScriptSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = JsonSerializer.Serialize(settings, LScriptJsonContext.Default.LScriptSettings);
        WriteAtomic(path, json);
    }

    internal static void WriteAtomic(
        string path,
        string contents,
        Action<string, string> commit = null
    )
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Legion settings path must be absolute.", nameof(path));
        }

        var parent = Path.GetDirectoryName(path)
                     ?? throw new InvalidOperationException($"Legion settings path '{path}' has no parent.");
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None
                   ))
            {
                using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    leaveOpen: true
                );
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (commit == null)
            {
                File.Move(temporary, path, overwrite: true);
            }
            else
            {
                commit(temporary, path);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception exception)
                {
                    Log.Error($"Unable to remove Legion settings temporary file '{temporary}': {exception}");
                }
            }
        }
    }
}
