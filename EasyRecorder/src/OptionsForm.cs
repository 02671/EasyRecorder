using System.Drawing.Drawing2D;

namespace EasyRecorder;

public sealed class OptionsForm : Form
{
    private readonly RadioButton[] _audioOptions;
    private readonly TextBox _folderBox;
    private readonly Label _ffmpegLabel;
    private string? _ffmpegPath;

    public string ResultFolder { get; private set; }
    public AudioMode ResultAudioMode { get; private set; }
    public string? ResultFfmpegPath { get; private set; }

    public OptionsForm(AppSettings settings)
    {
        ResultFolder = settings.OutputFolder;
        ResultAudioMode = settings.AudioMode;
        ResultFfmpegPath = settings.FfmpegPath;
        _ffmpegPath = settings.FfmpegPath;

        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Text = "选项";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(24, 24, 24);
        ForeColor = Color.White;
        ClientSize = new Size(640, 500);
        Font = UiFonts.Create(9.5f);
        KeyPreview = true;

        Label title = new()
        {
            Text = "选项",
            Font = UiFonts.Create(14f, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(24, 18),
            AutoSize = true
        };
        Controls.Add(title);

        Controls.Add(MakeSectionLabel("声音来源", 24, 58));

        _audioOptions = new[]
        {
            MakeRadio("录制系统声音（电脑里播放的声音）", 24, 86),
            MakeRadio("录制麦克风声音（你说话的声音）", 24, 116),
            MakeRadio("同时录制系统声音和麦克风", 24, 146),
            MakeRadio("不录声音", 24, 176)
        };

        _audioOptions[0].Checked = settings.AudioMode == AudioMode.System;
        _audioOptions[1].Checked = settings.AudioMode == AudioMode.Microphone;
        _audioOptions[2].Checked = settings.AudioMode == AudioMode.Both;
        _audioOptions[3].Checked = settings.AudioMode == AudioMode.None;
        if (!_audioOptions.Any(r => r.Checked))
        {
            _audioOptions[2].Checked = true;
        }

        Controls.Add(MakeSectionLabel("文件保存位置", 24, 218));

        _folderBox = new TextBox
        {
            Text = ResultFolder,
            Location = new Point(24, 242),
            Size = new Size(460, 30),
            BackColor = Color.FromArgb(42, 42, 42),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Font = UiFonts.Create(9.5f)
        };
        Controls.Add(_folderBox);

        ToolButton browseFolder = new("浏览...")
        {
            Location = new Point(494, 240),
            Size = new Size(120, 32)
        };
        browseFolder.Click += BrowseFolder_Click;
        Controls.Add(browseFolder);

        Controls.Add(MakeNote("录像会保存为 MP4 文件（视频 H.264，声音 AAC）。", 24, 280));

        Panel line = new()
        {
            Location = new Point(24, 312),
            Size = new Size(592, 1),
            BackColor = Color.FromArgb(58, 58, 58)
        };
        Controls.Add(line);

        Controls.Add(MakeSectionLabel("FFmpeg", 24, 326));

        _ffmpegLabel = new Label
        {
            Text = string.IsNullOrWhiteSpace(_ffmpegPath)
                ? "未找到（录制时会提示你选择）"
                : _ffmpegPath,
            Location = new Point(24, 356),
            Size = new Size(460, 30),
            ForeColor = Color.FromArgb(180, 180, 180),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft
        };
        Controls.Add(_ffmpegLabel);

        ToolButton browseFfmpeg = new("选择...")
        {
            Location = new Point(494, 354),
            Size = new Size(120, 32)
        };
        browseFfmpeg.Click += BrowseFfmpeg_Click;
        Controls.Add(browseFfmpeg);

        Controls.Add(MakeNote(
            "把 ffmpeg.exe 放到 EasyRecorder.exe 同一个文件夹即可自动识别；\n也可以点「选择」手动指定。",
            24,
            392,
            560,
            44));

        ToolButton cancel = new("取消")
        {
            Location = new Point(430, 448),
            Size = new Size(90, 34)
        };
        cancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };
        Controls.Add(cancel);

        ToolButton save = new("保存")
        {
            Location = new Point(530, 448),
            Size = new Size(90, 34),
            Accent = true
        };
        save.Click += Save_Click;
        Controls.Add(save);

        AcceptButton = null;
        CancelButton = null;
    }

    private Label MakeSectionLabel(string text, int x, int y)
    {
        return new Label
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            ForeColor = Color.FromArgb(150, 150, 150),
            Font = UiFonts.Create(9f)
        };
    }

    private RadioButton MakeRadio(string text, int x, int y)
    {
        RadioButton radio = new()
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            FlatStyle = FlatStyle.Flat,
            Font = UiFonts.Create(9.5f),
            Padding = new Padding(22, 2, 4, 2),
            UseVisualStyleBackColor = false
        };
        radio.FlatAppearance.BorderSize = 0;
        radio.FlatAppearance.CheckedBackColor = Color.Transparent;
        radio.FlatAppearance.MouseOverBackColor = Color.Transparent;
        radio.FlatAppearance.MouseDownBackColor = Color.Transparent;
        radio.CheckedChanged += (_, _) => radio.Invalidate();
        radio.Paint += Radio_Paint;
        Controls.Add(radio);
        return radio;
    }

    private static void Radio_Paint(object? sender, PaintEventArgs e)
    {
        if (sender is not RadioButton radio)
        {
            return;
        }

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int size = 14;
        int y = Math.Max(1, (radio.Height - size) / 2);
        Rectangle circle = new(4, y, size, size);
        using (SolidBrush fill = new(Color.FromArgb(42, 42, 42)))
        {
            g.FillEllipse(fill, circle);
        }

        using (Pen ring = new(Color.FromArgb(220, 220, 220), 1.6f))
        {
            g.DrawEllipse(ring, circle);
        }

        if (radio.Checked)
        {
            Rectangle dot = Rectangle.Inflate(circle, -4, -4);
            using SolidBrush brush = new(Color.FromArgb(255, 82, 82));
            g.FillEllipse(brush, dot);
        }
    }

    private Label MakeNote(string text, int x, int y, int width = 560, int height = 22)
    {
        return new Label
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(width, height),
            ForeColor = Color.FromArgb(138, 138, 138),
            Font = UiFonts.Create(8.8f)
        };
    }

    private void BrowseFolder_Click(object? sender, EventArgs e)
    {
        using FolderBrowserDialog dialog = new()
        {
            Description = "选择录像文件的保存位置",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_folderBox.Text)
                ? _folderBox.Text
                : AppSettings.DefaultOutputFolder(),
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _folderBox.Text = dialog.SelectedPath;
        }
    }

    private void BrowseFfmpeg_Click(object? sender, EventArgs e)
    {
        using OpenFileDialog dialog = new()
        {
            Title = "请选择 ffmpeg.exe",
            Filter = "FFmpeg 程序 (ffmpeg.exe)|ffmpeg.exe|可执行文件 (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (!FfmpegLocator.Validate(dialog.FileName))
        {
            MessageBox.Show(
                this,
                "这个文件不能正常运行，可能不是 Windows 版的 ffmpeg.exe。\n请换一个文件再试。",
                "FFmpeg 不可用",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        _ffmpegPath = dialog.FileName;
        _ffmpegLabel.Text = _ffmpegPath;
    }

    private void Save_Click(object? sender, EventArgs e)
    {
        string folder = _folderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = AppSettings.DefaultOutputFolder();
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch
        {
            MessageBox.Show(
                this,
                "这个保存位置无法使用，请换一个文件夹。",
                "保存位置不可用",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        ResultFolder = folder;
        ResultFfmpegPath = _ffmpegPath;

        if (_audioOptions[0].Checked)
        {
            ResultAudioMode = AudioMode.System;
        }
        else if (_audioOptions[1].Checked)
        {
            ResultAudioMode = AudioMode.Microphone;
        }
        else if (_audioOptions[2].Checked)
        {
            ResultAudioMode = AudioMode.Both;
        }
        else
        {
            ResultAudioMode = AudioMode.None;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
            return;
        }

        base.OnKeyDown(e);
    }

    public void ApplyTo(AppSettings settings)
    {
        settings.OutputFolder = ResultFolder;
        settings.AudioMode = ResultAudioMode;
        settings.FfmpegPath = ResultFfmpegPath;
    }
}
