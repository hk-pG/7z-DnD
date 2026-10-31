using System.Collections.Concurrent;

namespace SevenZipDnD;

public partial class Form1 : Form
{
    private readonly Label _dropLabel;
    private readonly ListView _jobList;
    private readonly TextBox _logBox;
    private readonly Label _statusLabel;
    private readonly Dictionary<string, ListViewItem> _jobItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ExtractProgress> _latestProgress = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer _uiFlushTimer;
    private readonly SemaphoreSlim _passwordUiLock = new(1, 1);
    private ArchiveExtractor? _extractor;
    private bool _busy;

    public Form1(string[] startupFiles)
    {
        InitializeComponent();
        Text = "7z DnD — ドロップで解凍";
        Width = 640;
        Height = 520;
        MinimumSize = new Size(480, 400);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        Font = new Font("Segoe UI", 10F);

        _dropLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 88,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "圧縮ファイルをここにドロップ\n（同名サブフォルダに並列解凍）",
            BackColor = Color.FromArgb(245, 248, 252),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 12F, FontStyle.Regular),
            Padding = new Padding(12),
            AllowDrop = true,
        };

        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "準備中…",
            Padding = new Padding(8, 0, 8, 0),
        };

        _jobList = new ListView
        {
            Dock = DockStyle.Top,
            Height = 160,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _jobList.Columns.Add("ファイル", 180);
        _jobList.Columns.Add("状態", 90);
        _jobList.Columns.Add("詳細", 320);

        _logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            Font = new Font("Consolas", 9.5F),
        };

        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        panel.Controls.Add(_logBox);
        panel.Controls.Add(_jobList);
        panel.Controls.Add(_dropLabel);
        Controls.Add(panel);
        Controls.Add(_statusLabel);

        _uiFlushTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _uiFlushTimer.Tick += (_, _) => FlushProgressToUi();
        _uiFlushTimer.Start();

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        _dropLabel.DragEnter += OnDragEnter;
        _dropLabel.DragDrop += OnDragDrop;

        Load += async (_, _) =>
        {
            InitializeSevenZip();
            if (startupFiles.Length > 0)
            {
                await ExtractFilesAsync(startupFiles);
            }
        };

        FormClosed += (_, _) => _uiFlushTimer.Stop();
    }

    private void InitializeSevenZip()
    {
        var location = SevenZipLocator.Find();
        if (location is null)
        {
            DisableSevenZip(
                statusText: "7-Zip が見つかりません",
                dropText: "7-Zip をインストールしてください",
                message: "エラー: 7z.exe が見つかりません。");
            return;
        }

        var path = location.Path;
        if (location.Source == SevenZipSource.PathEnvironment
            || !SevenZipLocator.IsUnderProgramFiles(path))
        {
            // Do not silently trust PATH / non-Program-Files binaries.
            var answer = MessageBox.Show(
                this,
                "Program Files 以外の場所で 7z.exe が見つかりました。\n\n"
                + path + "\n\n"
                + "改ざんされた実行ファイルの可能性があります。\n"
                + "このパスを信頼して使用しますか？\n\n"
                + "いいえ を選ぶと起動を中止します（推奨: 公式 7-Zip を Program Files にインストール）。",
                "7-Zip の確認",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
            {
                DisableSevenZip(
                    statusText: "7-Zip の使用が拒否されました",
                    dropText: "信頼できる 7-Zip を Program Files にインストールしてください",
                    message: "エラー: Program Files 以外の 7z.exe の使用が拒否されました。");
                return;
            }

            AppendLog("警告: ユーザー確認のうえ Program Files 以外の 7z.exe を使用します。");
            AppLog.Write($"7Z_PATH_CONFIRMED {path}");
        }

        _extractor = new ArchiveExtractor(path);
        _statusLabel.Text = $"7-Zip: {path}";
        _statusLabel.ForeColor = Color.DimGray;
        AppendLog($"準備完了 — {path}");
        AppendLog($"ログファイル: {AppLog.Path}");
        AppendLog($"並列数上限: {ArchiveExtractor.MaxConcurrency}（パスワード保護時はファイルごとに入力）");
    }

    private void DisableSevenZip(string statusText, string dropText, string message)
    {
        _statusLabel.Text = statusText;
        _statusLabel.ForeColor = Color.Firebrick;
        AppendLog(message);
        AppendLog(@"標準パス: C:\Program Files\7-Zip\7z.exe");
        _dropLabel.Text = dropText;
        _dropLabel.Enabled = false;
        AllowDrop = false;
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (_busy || _extractor is null)
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            e.Effect = DragDropEffects.Copy;
            return;
        }

        e.Effect = DragDropEffects.None;
    }

    private async void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (_busy || _extractor is null)
        {
            return;
        }

        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

        await ExtractFilesAsync(files);
    }

    private async Task ExtractFilesAsync(string[] paths)
    {
        if (_extractor is null || _busy)
        {
            return;
        }

        var archives = paths
            .Where(File.Exists)
            .Where(ArchiveExtractor.IsSupportedArchive)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var skipped = paths.Length - archives.Length;
        if (archives.Length == 0)
        {
            AppendLog("対応する圧縮ファイルがありません。");
            if (skipped > 0)
            {
                AppendLog($"スキップ: {skipped} 件");
            }
            return;
        }

        _busy = true;
        _dropLabel.Text = $"解凍中…（最大 {ArchiveExtractor.MaxConcurrency} 並列 / {archives.Length} 件）";
        _dropLabel.BackColor = Color.FromArgb(255, 248, 230);
        ResetJobList(archives);
        UpdateStatus($"進行中: 0 / {archives.Length}", Color.DarkOrange);

        var extractor = _extractor;
        var done = 0;
        var ok = 0;
        var ng = 0;
        var total = archives.Length;
        var gate = new SemaphoreSlim(ArchiveExtractor.MaxConcurrency);

        try
        {
            AppendLog($"並列解凍開始: {total} 件");
            AppLog.Write($"BATCH start count={total}");

            var tasks = archives.Select(archive => Task.Run(async () =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    // Custom progress: store latest only — timer flushes to UI (avoids BeginInvoke flood).
                    var progress = new ProgressBridge(OnProgressSampled);
                    var result = await extractor.ExtractAsync(
                        archive,
                        progress,
                        PromptPasswordAsync).ConfigureAwait(false);

                    var finished = Interlocked.Increment(ref done);
                    if (result.Success)
                    {
                        Interlocked.Increment(ref ok);
                    }
                    else
                    {
                        Interlocked.Increment(ref ng);
                    }

                    // Final state update (low volume); sanitize in case Error echoes -p secrets.
                    var finalMessage = result.Success
                        ? (result.OutputDirectory ?? "完了")
                        : AppLog.SanitizeForLog(result.Error ?? "失敗");
                    _latestProgress[archive] = new ExtractProgress(
                        archive,
                        result.Success ? ExtractStage.Succeeded : ExtractStage.Failed,
                        finalMessage,
                        result.Success ? 100 : null);

                    BeginInvoke(() =>
                    {
                        UpdateStatus($"進行中: {finished} / {total}", Color.DarkOrange);
                        if (result.Success)
                        {
                            AppendLog($"完了: {Path.GetFileName(result.ArchivePath)} → {result.OutputDirectory}");
                        }
                        else
                        {
                            AppendLog($"失敗: {Path.GetFileName(result.ArchivePath)} — {finalMessage}");
                        }
                    });

                    return result;
                }
                finally
                {
                    gate.Release();
                }
            })).ToArray();

            await Task.WhenAll(tasks).ConfigureAwait(true);
            FlushProgressToUi();

            if (skipped > 0)
            {
                AppendLog($"スキップ（非対応）: {skipped} 件");
            }

            AppendLog($"—— 成功 {ok} / 失敗 {ng} ——");
            AppLog.Write($"BATCH done ok={ok} ng={ng}");
            UpdateStatus(
                ng == 0 ? $"完了: {ok} 件" : $"完了: 成功 {ok}, 失敗 {ng}",
                ng == 0 ? Color.ForestGreen : Color.DarkOrange);
        }
        finally
        {
            _busy = false;
            _dropLabel.Text = "圧縮ファイルをここにドロップ\n（同名サブフォルダに並列解凍）";
            _dropLabel.BackColor = Color.FromArgb(245, 248, 252);
        }
    }

    private void OnProgressSampled(ExtractProgress update)
    {
        _latestProgress[update.ArchivePath] = update;

        // Password wait / start / end: wake UI sooner via a single BeginInvoke.
        if (update.Stage is ExtractStage.WaitingPassword or ExtractStage.Starting
            or ExtractStage.Succeeded or ExtractStage.Failed)
        {
            try
            {
                BeginInvoke(FlushProgressToUi);
            }
            catch (ObjectDisposedException)
            {
                // form closing
            }
        }
    }

    private void FlushProgressToUi()
    {
        if (IsDisposed)
        {
            return;
        }

        foreach (var pair in _latestProgress.ToArray())
        {
            if (!_jobItems.TryGetValue(pair.Key, out var item))
            {
                continue;
            }

            var update = pair.Value;
            item.SubItems[1].Text = StageLabel(update.Stage);
            var safeMessage = AppLog.SanitizeForLog(update.Message);
            var detail = update.Percent is int p
                ? $"{p}% {Truncate(safeMessage, 100)}"
                : Truncate(safeMessage, 120);
            item.SubItems[2].Text = detail;
        }
    }

    private async Task<string?> PromptPasswordAsync(string archivePath, CancellationToken cancellationToken)
    {
        await _passwordUiLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsDisposed)
            {
                return null;
            }

            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Show()
            {
                try
                {
                    using var dlg = new PasswordDialog(archivePath);
                    var result = dlg.ShowDialog(this);
                    // Do not log or echo password.
                    tcs.TrySetResult(result == DialogResult.OK ? dlg.Password : null);
                }
                catch (Exception ex)
                {
                    AppLog.WriteException("PASSWORD_DIALOG", ex);
                    tcs.TrySetResult(null);
                }
            }

            if (InvokeRequired)
            {
                BeginInvoke(Show);
            }
            else
            {
                Show();
            }

            await using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            _passwordUiLock.Release();
        }
    }

    private void ResetJobList(string[] archives)
    {
        _jobList.BeginUpdate();
        _jobList.Items.Clear();
        _jobItems.Clear();
        _latestProgress.Clear();

        foreach (var archive in archives)
        {
            var item = new ListViewItem(Path.GetFileName(archive));
            item.SubItems.Add("待機");
            item.SubItems.Add(archive);
            _jobList.Items.Add(item);
            _jobItems[archive] = item;
            _latestProgress[archive] = new ExtractProgress(archive, ExtractStage.Queued, archive, null);
        }

        _jobList.EndUpdate();
    }

    private static string StageLabel(ExtractStage stage) => stage switch
    {
        ExtractStage.Queued => "待機",
        ExtractStage.Starting => "開始",
        ExtractStage.Running => "解凍中",
        ExtractStage.WaitingPassword => "PW待ち",
        ExtractStage.Succeeded => "完了",
        ExtractStage.Failed => "失敗",
        _ => stage.ToString(),
    };

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "…";

    private void UpdateStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    private void AppendLog(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(message));
            return;
        }

        var safe = AppLog.SanitizeForLog(message);
        AppLog.Write(safe);
        var line = $"[{DateTime.Now:HH:mm:ss}] {safe}";
        if (_logBox.TextLength == 0)
        {
            _logBox.Text = line;
        }
        else
        {
            _logBox.AppendText(Environment.NewLine + line);
        }
    }

    /// <summary>
    /// IProgress that does NOT capture WinForms SynchronizationContext,
    /// so Report() from 7z callbacks won't queue thousands of UI messages.
    /// </summary>
    private sealed class ProgressBridge : IProgress<ExtractProgress>
    {
        private readonly Action<ExtractProgress> _handler;

        public ProgressBridge(Action<ExtractProgress> handler) => _handler = handler;

        public void Report(ExtractProgress value) => _handler(value);
    }
}
