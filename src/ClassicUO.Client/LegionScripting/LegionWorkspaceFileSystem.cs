using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ClassicUO.LegionScripting;

internal static class LegionWorkspaceFileSystem
{
    public static void WriteAllText(string path, string contents, Encoding encoding = null)
    {
        path = LegionWorkspacePaths.Current.EnsureScriptPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}"
        );
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
                    encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    leaveOpen: true
                );
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static void MoveFile(string source, string destination)
    {
        File.Move(
            LegionWorkspacePaths.Current.EnsureScriptPath(source),
            LegionWorkspacePaths.Current.EnsureScriptPath(destination)
        );
    }

    public static void MoveDirectory(string source, string destination)
    {
        Directory.Move(
            LegionWorkspacePaths.Current.EnsureScriptPath(source),
            LegionWorkspacePaths.Current.EnsureScriptPath(destination)
        );
    }

    public static void DeleteFile(string path)
    {
        File.Delete(LegionWorkspacePaths.Current.EnsureScriptPath(path));
    }

    public static void DeleteDirectory(string path, bool recursive)
    {
        path = LegionWorkspacePaths.Current.EnsureScriptPath(path);
        if (path.Equals(
                LegionWorkspacePaths.Current.ScriptsDirectory,
                OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal
            ))
        {
            throw new InvalidOperationException("The Legion script root itself cannot be deleted.");
        }

        Directory.Delete(path, recursive);
    }

    public static void WriteZipEntry(string zipPath, string entryPath, string contents)
    {
        zipPath = LegionWorkspacePaths.Current.EnsureScriptPath(zipPath);
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Update);
        archive.GetEntry(entryPath)?.Delete();
        var entry = archive.CreateEntry(entryPath);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(contents);
    }

    public static void DeleteZipEntry(string zipPath, string entryPath)
    {
        zipPath = LegionWorkspacePaths.Current.EnsureScriptPath(zipPath);
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Update);
        archive.GetEntry(entryPath)?.Delete();
    }
}
