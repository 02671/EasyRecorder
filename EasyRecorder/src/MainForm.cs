using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace EasyRecorder;

public sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly Recorder _recorder = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 200 };

    private readonly ToolButton _areaButton;
    private readonly ToolButton _recordButton;
    private readonly ToolButton _pauseButton;
    private readonly ToolButton _stopButton;
    private readonly ToolButton _cancelButton;
    private readonly ToolButton _optionsButton;
    private readonly ToolButton _closeButton;
    private readonly Label _statusLabel;
    private readonly Label _timerLabel;

    private Rectangle _region;
    private readonly RegionBorderForm _regionBorder = new();
    private bool _customRegion;
    private bool _saving;
    private bool _closePrompting;
    private bool _allowClose;
    private bool _firstShown;

    public MainForm()
    {
        _settings = AppSettings.Load();
        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                Icon = Icon.ExtractAssociatedIcon(exePath);
            }
        }
        catch
        {
        }

        Text = "EasyRecorder 录屏";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = true;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        BackColor = Color.FromArgb(24, 24, 24);
        ForeColor = Color.White;
        Font = UiFonts.Create(8.5f);
        ClientSize = new Size(640, 42);
        Opacity = 0;
        DoubleBuffered = true;
        KeyPreview = true;

        _areaButton = new ToolButton("区域");
        _recordButton = new ToolButton("录制") { Accent = true };
        _pauseButton = new ToolButton("暂停");
        _stopButton = new ToolButton("结束");
        _cancelButton = new ToolButton("取消");
        _optionsButton = new ToolButton("选项");
        _closeButton = new ToolButton("关闭") { Danger = true };

        _statusLabel = new Label
        {
            Text = "● 未录制",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(160, 160, 160),
            BackColor = Color.Transparent,
            Font = UiFonts.Create(8.5f)
        };

        _timerLabel = new Label
        {
            Text = "00:00:00",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(235, 235, 235),
            BackColor = Color.Transparent,
            Font = UiFonts.CreateMono(9.5f)
        };

        BuildLayout();

        _areaButton.Click += AreaButton_Click;
        _recordButton.Click += RecordButton_Click;
        _pauseButton.Click += PauseButton_Click;
        _stopButton.Click += StopButton_Click;
        _cancelButton.Click += CancelButton_Click;
        _optionsButton.Click += OptionsButton_Click;
        _closeButton.Click += (_, _) => Close();

        MouseDown += DragArea_MouseDown;
        _statusLabel.MouseDown += DragArea_MouseDown;
        _timerLabel.MouseDown += DragArea_MouseDown;

        _recorder.WarningRaised += message =>
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            try
            {
                BeginInvoke(() =>
                {
                    if (!IsDisposed)
                    {
                        MessageBox.Show(
                            this,
                            message,
                            "录制提示",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }
                });
            }
            catch
            {
            }
        };

        _uiTimer.Tick += (_, _) =>
        {
            if (_recorder.IsRecording)
            {
                TimeSpan elapsed = _recorder.Elapsed;
                _timerLabel.Text = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
            }
        };

        SetIdleUi();
    }

    private void BuildLayout()
    {
        const int pad = 8;
        const int gap = 4;
        const int buttonWidth = 52;
        const int buttonHeight = 26;
        const int statusWidth = 76;
        const int timerWidth = 78;
        const int closeWidth = 52;

        int y = (ClientSize.Height - buttonHeight) / 2;
        int x = pad;

        void AddButton(ToolButton button, int width)
        {
            button.Location = new Point(x, y);
            button.Size = new Size(width, buttonHeight);
            Controls.Add(button);
            x += width;
        }

        void AddSeparator()
        {
            x += gap;
            Panel separator = new()
            {
                Location = new Point(x, (ClientSize.Height - 16) / 2),
                Size = new Size(1, 16),
                BackColor = Color.FromArgb(58, 58, 58)
            };
            Controls.Add(separator);
            x += 1 + gap;
        }

        AddButton(_areaButton, buttonWidth);
        AddSeparator();
        AddButton(_recordButton, buttonWidth);
        AddSeparator();
        AddButton(_pauseButton, buttonWidth);
        AddSeparator();
        AddButton(_stopButton, buttonWidth);
        AddSeparator();
        AddButton(_cancelButton, buttonWidth);
        AddSeparator();
        AddButton(_optionsButton, buttonWidth);
        AddSeparator();

        _statusLabel.Location = new Point(x, 0);
        _statusLabel.Size = new Size(statusWidth, ClientSize.Height);
        Controls.Add(_statusLabel);
        x += statusWidth;

        AddSeparator();

        _timerLabel.Location = new Point(x, 0);
        _timerLabel.Size = new Size(timerWidth, ClientSize.Height);
        Controls.Add(_timerLabel);
        x += timerWidth;

        AddSeparator();
        AddButton(_closeButton, closeWidth);

        ClientSize = new Size(x + pad, ClientSize.Height);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (!_firstShown)
        {
            _firstShown = true;
            PositionAtTop();
            Opacity = 1;
            NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
            ShowWelcomeOnce();
            _uiTimer.Start();
        }
    }

    private void PositionAtTop()
    {
        Rectangle working = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(
            working.Left + Math.Max(0, (working.Width - Width) / 2),
            working.Top + 10);
    }

    private void ShowWelcomeOnce()
    {
        if (_settings.WelcomeShown)
        {
            return;
        }

        _settings.WelcomeShown = true;
        _settings.Save();

        MessageBox.Show(
            this,
            "欢迎使用 EasyRecorder。\n\n" +
            "1. 点「区域」框选画面；直接点「录制」则录制整个主屏幕。\n" +
            "2. 点「结束」保存文件；点「取消」不保存。\n" +
            "3. 点「选项」可以改声音来源和保存位置。\n\n" +
            "录像默认保存到「视频」文件夹里的 EasyRecorder 文件夹。",
            "欢迎使用",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void DragArea_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(
            Handle,
            NativeMethods.WM_NCLBUTTONDOWN,
            (IntPtr)NativeMethods.HTCAPTION,
            IntPtr.Zero);
    }

    private void AreaButton_Click(object? sender, EventArgs e)
    {
        if (_recorder.IsRecording || _saving)
        {
            return;
        }

        Point previous = Location;
        _regionBorder.Hide();
        Hide();

        Rectangle? selected = null;
        using (RegionSelectorForm selector = new())
        {
            selector.ShowDialog();
            if (selector.Confirmed)
            {
                selected = selector.SelectedRegion;
            }
        }

        TopMost = true;
        Show();
        Activate();

        if (selected is { Width: >= 16, Height: >= 16 })
        {
            _region = selected.Value;
            _customRegion = true;
            PlaceNearRegion(_region);
            _regionBorder.ShowAround(_region);
            SetIdleUi();
        }
        else
        {
            Location = previous;
            if (_customRegion)
            {
                _regionBorder.ShowAround(_region);
            }
        }
    }

    private void PlaceNearRegion(Rectangle region)
    {
        Rectangle working = Screen.FromRectangle(region).WorkingArea;
        int x = region.Left + (region.Width - Width) / 2;
        int y = region.Top - Height - 8;
        if (y < working.Top + 4)
        {
            y = region.Bottom + 8;
        }

        if (y + Height > working.Bottom)
        {
            y = working.Top + 8;
        }

        x = Math.Clamp(x, working.Left + 6, Math.Max(working.Left + 6, working.Right - Width - 6));
        Location = new Point(x, y);
    }

    private async void RecordButton_Click(object? sender, EventArgs e)
    {
        if (_recorder.IsRecording || _saving)
        {
            return;
        }

        if (_region.Width < 16 || _region.Height < 16)
        {
            Rectangle bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
            _region = new Rectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        }

        _recordButton.Enabled = false;
        try
        {
            await _recorder.StartAsync(_region, _settings.OutputFolder, _settings.AudioMode);
            SetRecordingUi();
        }
        catch (Exception ex)
        {
            SetIdleUi();
            MessageBox.Show(
                this,
                "无法开始录制：\n\n" + ex.Message,
                "录制失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void PauseButton_Click(object? sender, EventArgs e)
    {
        if (!_recorder.IsRecording)
        {
            return;
        }

        if (_recorder.IsPaused)
        {
            _recorder.Resume();
            _pauseButton.Text = "暂停";
            _statusLabel.Text = "● 录制中";
            _statusLabel.ForeColor = Color.FromArgb(255, 82, 82);
        }
        else
        {
            _recorder.Pause();
            _pauseButton.Text = "继续";
            _statusLabel.Text = "● 已暂停";
            _statusLabel.ForeColor = Color.FromArgb(255, 183, 77);
        }
    }

    private async void StopButton_Click(object? sender, EventArgs e)
    {
        await StopAndSaveAsync(false, true);
    }

    private async void CancelButton_Click(object? sender, EventArgs e)
    {
        if (!_recorder.IsRecording)
        {
            if (_customRegion && !_saving)
            {
                ClearSelectedRegion();
            }

            return;
        }

        DialogResult result = MessageBox.Show(
            this,
            "确定要取消本次录制吗？\n取消后这段录像不会保存。",
            "取消录制",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result == DialogResult.Yes)
        {
            await StopAndSaveAsync(true, false);
        }
    }

    private async Task StopAndSaveAsync(bool discard, bool showResult)
    {
        if (!_recorder.IsRecording || _saving)
        {
            return;
        }

        _saving = true;
        _uiTimer.Stop();
        SetSavingUi(discard ? "● 正在取消" : "● 正在保存");

        string? path = null;
        Exception? error = null;

        try
        {
            path = await _recorder.StopAsync(discard);
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            _saving = false;
            _uiTimer.Start();
            SetIdleUi();
        }

        if (error != null)
        {
            MessageBox.Show(
                this,
                "保存录像时出错：\n\n" + error.Message,
                "保存失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        if (discard)
        {
            return;
        }

        if (path != null && showResult)
        {
            DialogResult open = MessageBox.Show(
                this,
                "录像已保存到：\n" + path + "\n\n是否打开所在文件夹？",
                "保存成功",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            if (open == DialogResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = "/select,\"" + path + "\"",
                        UseShellExecute = true
                    });
                }
                catch
                {
                }
            }
        }
    }

    private void OptionsButton_Click(object? sender, EventArgs e)
    {
        if (_recorder.IsRecording || _saving)
        {
            return;
        }

        using OptionsForm options = new(_settings);
        if (options.ShowDialog(this) == DialogResult.OK)
        {
            options.ApplyTo(_settings);
            _settings.Save();
        }
    }

    private void SetIdleUi()
    {
        _areaButton.Enabled = true;
        _recordButton.Enabled = true;
        _pauseButton.Enabled = false;
        _stopButton.Enabled = false;
        _cancelButton.Enabled = _customRegion;
        _optionsButton.Enabled = true;
        _pauseButton.Text = "暂停";
        _statusLabel.Text = "● 未录制";
        _statusLabel.ForeColor = Color.FromArgb(160, 160, 160);
        _timerLabel.Text = "00:00:00";
    }

    private void SetRecordingUi()
    {
        _areaButton.Enabled = false;
        _recordButton.Enabled = false;
        _pauseButton.Enabled = true;
        _stopButton.Enabled = true;
        _cancelButton.Enabled = true;
        _optionsButton.Enabled = false;
        _pauseButton.Text = "暂停";
        _statusLabel.Text = "● 录制中";
        _statusLabel.ForeColor = Color.FromArgb(255, 82, 82);
    }

    private void ClearSelectedRegion()
    {
        _customRegion = false;
        _region = Rectangle.Empty;
        _regionBorder.Hide();
        _cancelButton.Enabled = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && !_recorder.IsRecording && !_saving && _customRegion)
        {
            ClearSelectedRegion();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void SetSavingUi(string status)
    {
        _areaButton.Enabled = false;
        _recordButton.Enabled = false;
        _pauseButton.Enabled = false;
        _stopButton.Enabled = false;
        _cancelButton.Enabled = false;
        _optionsButton.Enabled = false;
        _statusLabel.Text = status;
        _statusLabel.ForeColor = Color.FromArgb(100, 181, 246);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using Pen border = new(Color.FromArgb(48, 48, 48));
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel)
        {
            return;
        }

        if (_allowClose)
        {
            return;
        }

        if (_saving)
        {
            e.Cancel = true;
            MessageBox.Show(
                this,
                "正在保存录像，请稍等几秒再关闭。",
                "请稍候",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (!_recorder.IsRecording)
        {
            return;
        }

        e.Cancel = true;
        if (_closePrompting)
        {
            return;
        }

        _closePrompting = true;
        DialogResult result = MessageBox.Show(
            this,
            "正在录制中。\n关闭前会先结束并保存本次录像，是否继续？",
            "确认关闭",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        _closePrompting = false;

        if (result != DialogResult.Yes)
        {
            return;
        }

        await StopAndSaveAsync(false, true);
        _allowClose = true;
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _uiTimer.Stop();
        _uiTimer.Dispose();
        _regionBorder.Close();
        _regionBorder.Dispose();
        _recorder.Dispose();
        _settings.Save();
        base.OnFormClosed(e);
    }
}
