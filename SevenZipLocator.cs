namespace SevenZipDnD;

internal enum SevenZipSource
{
    ProgramFiles,
    PathEnvironment,
}

internal sealed record SevenZipLocation(string Path, SevenZipSource Source);

internal static class SevenZipLocator
{
    private static readonly string[] CandidatePaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"),
    ];

    /// <summary>
    /// Prefer Program Files / Program Files (x86) only.
    /// PATH hits are returned separately so the UI can require explicit confirmation.
    /// </summary>
    public static SevenZipLocation? Find()
    {
        foreach (var path in CandidatePaths)
        {
            if (File.Exists(path))
            {
                return new SevenZipLocation(path, SevenZipSource.ProgramFiles);
            }
        }

        var fromPath = FindOnPath("7z.exe");
        if (fromPath is not null)
        {
            return new SevenZipLocation(fromPath, SevenZipSource.PathEnvironment);
        }

        return null;
    }

    public static bool IsUnderProgramFiles(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     })
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                var prefix = Path.GetFullPath(root);
                if (!prefix.EndsWith(Path.DirectorySeparatorChar))
                {
                    prefix += Path.DirectorySeparatorChar;
                }

                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static string? FindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(dir, fileName));
                if (File.Exists(candidate))
                {
                    // Still classify as PATH even if the resolved path happens to be under Program Files;
                    // Find() already preferred the standard install locations first.
                    return candidate;
                }
            }
            catch
            {
                // ignore bad PATH entries
            }
        }

        return null;
    }
}
