namespace SevenZipDnD;

internal sealed class PasswordDialog : Form
{
    private readonly TextBox _passwordBox;

    public string Password => _passwordBox.Text;

    public PasswordDialog(string archivePath)
    {
        Text = "パスワードが必要です";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(500, 190);
        Font = new Font("Segoe UI", 10F);

        var title = new Label
        {
            AutoSize = false,
            Location = new Point(16, 12),
            Size = new Size(468, 24),
            Text = "このアーカイブはパスワードで保護されています。",
        };

        var fileLabel = new Label
        {
            AutoSize = false,
            Location = new Point(16, 40),
            Size = new Size(468, 24),
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Text = Path.GetFileName(archivePath),
        };

        var pathLabel = new Label
        {
            AutoSize = false,
            Location = new Point(16, 64),
            Size = new Size(468, 36),
            ForeColor = Color.DimGray,
            Text = archivePath,
        };

        var passwordCaption = new Label
        {
            AutoSize = true,
            Location = new Point(16, 112),
            Text = "パスワード:",
        };

        _passwordBox = new TextBox
        {
            Location = new Point(110, 108),
            Size = new Size(374, 27),
            UseSystemPasswordChar = true,
        };

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(284, 148),
            Size = new Size(90, 28),
        };

        var cancel = new Button
        {
            Text = "キャンセル",
            DialogResult = DialogResult.Cancel,
            Location = new Point(384, 148),
            Size = new Size(100, 28),
        };

        AcceptButton = ok;
        CancelButton = cancel;

        Controls.AddRange([title, fileLabel, pathLabel, passwordCaption, _passwordBox, ok, cancel]);
        Shown += (_, _) => _passwordBox.Focus();
    }
}
