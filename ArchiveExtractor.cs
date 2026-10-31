using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SevenZipDnD;

internal sealed class ArchiveExtractor
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z", ".zip", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2",
        ".xz", ".txz", ".lzma", ".cab", ".iso", ".wim", ".arj", ".lzh",
        ".lha", ".z", ".001", ".jar", ".apk",
    };

    private static readonly Regex PercentRegex = new(@"(\d+)\s*%", RegexOptions.Compiled);

    public const int MaxConcurrency = 2;

    private readonly string _sevenZipPath;

    public ArchiveExtractor(string sevenZipPath)
    {
        _sevenZipPath = sevenZipPath;
    }

    public static bool IsSupportedArchive(string path)
    {
        var ext = Path.GetExtension(path);
        if (SupportedExtensions.Contains(ext))
        {
            return true;
        }

        var name = Path.GetFileName(path);
        return name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tar.lzma", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// passwordProvider: return password string (may be empty), or null if user cancelled.
    /// </summary>
    public async Task<ExtractResult> ExtractAsync(
        string archivePath,
        IProgress<ExtractProgress>? progress = null,
        Func<string, CancellationToken, Task<string?>>? passwordProvider = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath))
        {
            return ExtractResult.Fail(archivePath, "ファイルが見つかりません。");
        }

        var parentDir = Path.GetDirectoryName(archivePath);
        if (string.IsNullOrWhiteSpace(parentDir))
        {
            return ExtractResult.Fail(archivePath, "出力先フォルダを特定できません。");
        }

        var outputDir = Path.Combine(parentDir, GetArchiveBaseName(archivePath));
        Directory.CreateDirectory(outputDir);

        string? password = null;
        var promptCount = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new ExtractProgress(
                archivePath,
                ExtractStage.Starting,
                password is null ? $"開始 → {outputDir}" : "パスワードで再試行中…",
                null));

            AppLog.Write(password is null
                ? $"START {archivePath} -> {outputDir}"
                : $"RETRY_WITH_PASSWORD {archivePath} attempt={promptCount}");

            var run = await RunSevenZipAsync(
                archivePath,
                outputDir,
                password,
                progress,
                cancellationToken).ConfigureAwait(false);

            if (run.ExitCode == 0)
            {
                // Practical zip-slip mitigation: refuse / remove entries whose resolved path
                // escapes outputDir (symlinks / reparse points / odd path forms).
                // TODO: Absolute paths written *outside* outputDir by 7z are not visible via a
                // walk of outputDir alone; full prevention needs archive listing or 7z API control.
                var slip = ScanForPathEscape(outputDir);
                if (slip.EscapedCount > 0)
                {
                    var msg = $"危険なパスを検出したため拒否しました（{slip.EscapedCount} 件を削除）。信頼できるアーカイブのみ解凍してください。";
                    AppLog.Write($"ZIPSLIP {archivePath}: removed={slip.EscapedCount}");
                    progress?.Report(new ExtractProgress(archivePath, ExtractStage.Failed, msg, null));
                    return ExtractResult.Fail(archivePath, msg);
                }

                AppLog.Write($"OK {archivePath} -> {outputDir}");
                progress?.Report(new ExtractProgress(archivePath, ExtractStage.Succeeded, outputDir, 100));
                return ExtractResult.Ok(archivePath, outputDir);
            }

            if (IsPasswordRelatedFailure(run) && passwordProvider is not null)
            {
                promptCount++;
                AppLog.Write(promptCount == 1
                    ? $"PASSWORD_REQUIRED {archivePath}"
                    : $"PASSWORD_WRONG {archivePath}");

                progress?.Report(new ExtractProgress(
                    archivePath,
                    ExtractStage.WaitingPassword,
                    promptCount == 1 ? "パスワード入力待ち…" : "パスワードが違うようです。再入力…",
                    null));

                password = await passwordProvider(archivePath, cancellationToken).ConfigureAwait(false);
                if (password is null)
                {
                    const string msg = "パスワード入力がキャンセルされました。";
                    AppLog.Write($"PASSWORD_CANCEL {archivePath}");
                    progress?.Report(new ExtractProgress(archivePath, ExtractStage.Failed, msg, null));
                    return ExtractResult.Fail(archivePath, msg);
                }

                continue;
            }

            var rawDetail = string.IsNullOrWhiteSpace(run.ErrorText)
                ? $"7z が終了コード {run.ExitCode} で失敗しました。"
                : run.ErrorText;
            var detail = AppLog.SanitizeForLog(rawDetail);

            AppLog.Write($"FAIL {archivePath}: exit={run.ExitCode} {detail}");
            progress?.Report(new ExtractProgress(archivePath, ExtractStage.Failed, detail, null));
            return ExtractResult.Fail(archivePath, detail);
        }
    }

    private async Task<SevenZipRunResult> RunSevenZipAsync(
        string archivePath,
        string outputDir,
        string? password,
        IProgress<ExtractProgress>? progress,
        CancellationToken cancellationToken)
    {
        var args = new StringBuilder();
        args.Append("x ").Append(QuotePathArgument(archivePath));
        args.Append(" -o").Append(QuotePathArgument(outputDir));
        args.Append(" -y -bsp1 -bse1");
        // Always pass -p so 7z does not hang waiting on console stdin.
        // Empty means "no password" / try empty.
        // Known limitation: -pPASSWORD is visible on the process command line to local admins / other processes.
        args.Append(" -p").Append(EscapePasswordArgument(password ?? string.Empty));

        var psi = new ProcessStartInfo
        {
            FileName = _sevenZipPath,
            Arguments = args.ToString(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.Default,
            StandardErrorEncoding = Encoding.Default,
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var combined = new StringBuilder();
        var gate = new object();
        var lastUiUtc = DateTime.MinValue;
        var lastLoggedPercent = -5;

        void HandleLine(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            var line = raw.Trim();
            var safeLine = AppLog.SanitizeForLog(line);
            lock (combined)
            {
                combined.AppendLine(safeLine);
            }

            var percent = TryParsePercent(line);
            var now = DateTime.UtcNow;
            var force = line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Everything is Ok", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Wrong password", StringComparison.OrdinalIgnoreCase);

            bool report;
            lock (gate)
            {
                var jumped = percent is int p && p >= lastLoggedPercent + 5;
                report = force || jumped || (now - lastUiUtc) >= TimeSpan.FromMilliseconds(400);
                if (report)
                {
                    lastUiUtc = now;
                    if (percent is int p2)
                    {
                        lastLoggedPercent = p2;
                    }
                }
            }

            if (!report)
            {
                return;
            }

            AppLog.Write($"PROG {Path.GetFileName(archivePath)}: {safeLine}");
            progress?.Report(new ExtractProgress(archivePath, ExtractStage.Running, safeLine, percent));
        }

        try
        {
            if (!process.Start())
            {
                return new SevenZipRunResult(-1, "7z.exe の起動に失敗しました。");
            }

            AppLog.Write($"PROC pid={process.Id} {archivePath}");

            // Close stdin so 7z cannot block on interactive password prompt.
            process.StandardInput.Close();

            process.OutputDataReceived += (_, e) => HandleLine(e.Data);
            process.ErrorDataReceived += (_, e) => HandleLine(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new SevenZipRunResult(process.ExitCode, combined.ToString().Trim());
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // ignore
            }

            throw;
        }
    }

    /// <summary>
    /// After extract, delete any entry whose fully-resolved path escapes <paramref name="outputDir"/>.
    /// </summary>
    internal static PathEscapeScanResult ScanForPathEscape(string outputDir)
    {
        var removed = 0;
        if (!Directory.Exists(outputDir))
        {
            return new PathEscapeScanResult(0);
        }

        string root;
        try
        {
            root = Path.GetFullPath(outputDir);
        }
        catch
        {
            return new PathEscapeScanResult(0);
        }

        if (!root.EndsWith(Path.DirectorySeparatorChar) && !root.EndsWith(Path.AltDirectorySeparatorChar))
        {
            root += Path.DirectorySeparatorChar;
        }

        // Files first, then directories (deepest first) so we can remove escaped trees.
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(outputDir, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            AppLog.WriteException("ZIPSLIP_ENUM", ex);
            return new PathEscapeScanResult(0);
        }

        foreach (var entry in entries)
        {
            if (!TryResolvePath(entry, out var resolved))
            {
                continue;
            }

            if (IsUnderRoot(resolved, root))
            {
                // Also flag reparse points whose *target* escapes the output dir.
                if (TryGetReparseTarget(entry, out var target) && !IsUnderRoot(target, root))
                {
                    if (TryDeleteEntry(entry))
                    {
                        removed++;
                        AppLog.Write($"ZIPSLIP_REMOVE reparse -> {AppLog.SanitizeForLog(target)}");
                    }
                }

                continue;
            }

            if (TryDeleteEntry(entry))
            {
                removed++;
                AppLog.Write($"ZIPSLIP_REMOVE {AppLog.SanitizeForLog(resolved)}");
            }
        }

        return new PathEscapeScanResult(removed);
    }

    private static bool TryResolvePath(string path, out string resolved)
    {
        try
        {
            resolved = Path.GetFullPath(path);
            return true;
        }
        catch
        {
            resolved = string.Empty;
            return false;
        }
    }

    private static bool IsUnderRoot(string fullPath, string rootWithSep)
    {
        try
        {
            var full = Path.GetFullPath(fullPath);
            return full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    rootWithSep.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetReparseTarget(string path, out string target)
    {
        target = string.Empty;
        try
        {
            var attr = File.GetAttributes(path);
            if ((attr & FileAttributes.ReparsePoint) == 0)
            {
                return false;
            }

            if ((attr & FileAttributes.Directory) != 0)
            {
                var info = new DirectoryInfo(path);
                var link = info.LinkTarget;
                if (string.IsNullOrWhiteSpace(link))
                {
                    return false;
                }

                target = Path.IsPathRooted(link)
                    ? Path.GetFullPath(link)
                    : Path.GetFullPath(Path.Combine(path, link));
                return true;
            }
            else
            {
                var info = new FileInfo(path);
                var link = info.LinkTarget;
                if (string.IsNullOrWhiteSpace(link))
                {
                    return false;
                }

                target = Path.IsPathRooted(link)
                    ? Path.GetFullPath(link)
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, link));
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDeleteEntry(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            if ((attr & FileAttributes.Directory) != 0
                && (attr & FileAttributes.ReparsePoint) == 0)
            {
                Directory.Delete(path, recursive: true);
            }
            else if ((attr & FileAttributes.Directory) != 0)
            {
                Directory.Delete(path);
            }
            else
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLog.WriteException("ZIPSLIP_DELETE", ex);
            return false;
        }
    }

    internal static bool IsPasswordRelatedFailure(SevenZipRunResult run)
    {
        if (run.ExitCode == 0)
        {
            return false;
        }

        var text = run.ErrorText ?? string.Empty;
        return text.Contains("Wrong password", StringComparison.OrdinalIgnoreCase)
            || text.Contains("wrong passphrase", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Enter password", StringComparison.OrdinalIgnoreCase)
            || text.Contains("password required", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Cannot open encrypted", StringComparison.OrdinalIgnoreCase)
            || text.Contains("パスワード", StringComparison.Ordinal);
    }

    /// <summary>
    /// Quote a filesystem path for ProcessStartInfo.Arguments / 7z -o.
    /// Windows forbids '"' in paths; strip if present. Harden trailing '\' so it cannot escape the closing quote.
    /// </summary>
    private static string QuotePathArgument(string path)
    {
        path = path.Replace("\"", string.Empty);
        if (path.EndsWith('\\') || path.EndsWith('/'))
        {
            return "\"" + path + "\\\"";
        }

        return "\"" + path + "\"";
    }

    /// <summary>
    /// 7z -pPASSWORD: no space after -p. Quote if needed for cmd metacharacters.
    /// </summary>
    private static string EscapePasswordArgument(string password)
    {
        // Avoid breaking our ProcessStartInfo Arguments parsing.
        if (password.Length == 0)
        {
            return "\"\"";
        }

        if (password.IndexOfAny([' ', '"', '&', '|', '<', '>', '^']) >= 0)
        {
            return "\"" + password.Replace("\"", "\\\"") + "\"";
        }

        return password;
    }

    private static int? TryParsePercent(string line)
    {
        var m = PercentRegex.Match(line);
        if (!m.Success)
        {
            return null;
        }

        return int.TryParse(m.Groups[1].Value, out var p) ? p : null;
    }

    internal static string GetArchiveBaseName(string archivePath)
    {
        var name = Path.GetFileName(archivePath);
        string[] compoundExtensions =
        [
            ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.lzma",
        ];

        foreach (var ext in compoundExtensions)
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^ext.Length];
            }
        }

        return Path.GetFileNameWithoutExtension(name);
    }
}

internal sealed record PathEscapeScanResult(int EscapedCount);

internal enum ExtractStage
{
    Queued,
    Starting,
    Running,
    WaitingPassword,
    Succeeded,
    Failed,
}

internal sealed record ExtractProgress(
    string ArchivePath,
    ExtractStage Stage,
    string Message,
    int? Percent);

internal sealed record SevenZipRunResult(int ExitCode, string ErrorText);

internal sealed record ExtractResult(string ArchivePath, bool Success, string? OutputDirectory, string? Error)
{
    public static ExtractResult Ok(string archivePath, string outputDir) =>
        new(archivePath, true, outputDir, null);

    public static ExtractResult Fail(string archivePath, string error) =>
        new(archivePath, false, null, error);
}
