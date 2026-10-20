using System.Drawing.Drawing2D;

namespace EasyRecorder;

public sealed class RegionSelectorForm : Form
{
    private Bitmap? _screenshot;
    private Rectangle _virtualScreen;
    private Point _start;
    private Rectangle _selection;
    private bool _dragging;
    private readonly Font _font = UiFonts.Create(10.5f);
    private readonly Font _hintFont = UiFonts.Create(10f);

    public Rectangle SelectedRegion { get; private set; }
    public bool Confirmed { get; private set; }

    public RegionSelectorForm()
    {
        _virtualScreen = SystemInformation.VirtualScreen;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = _virtualScreen;
        ShowInTaskbar = false;
        TopMost = true;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = Color.Black;
        Text = "选择录制区域";

        try
        {
            _screenshot = new Bitmap(_virtualScreen.Width, _virtualScreen.Height);
            using Graphics graphics = Graphics.FromImage(_screenshot);
            graphics.CopyFromScreen(
                _virtualScreen.Location,
                Point.Empty,
                _virtualScreen.Size,
                CopyPixelOperation.SourceCopy);
        }
        catch
        {
            _screenshot?.Dispose();
            _screenshot = null;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Confirmed = false;
            Close();
            return;
        }

        if (e.KeyCode == Keys.Enter && _selection.Width >= 32 && _selection.Height >= 32)
        {
            ConfirmSelection();
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _dragging = true;
        _start = new Point(
            Math.Clamp(e.X, 0, ClientSize.Width - 1),
            Math.Clamp(e.Y, 0, ClientSize.Height - 1));
        _selection = new Rectangle(_start, Size.Empty);
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        int x = Math.Clamp(e.X, 0, ClientSize.Width);
        int y = Math.Clamp(e.Y, 0, ClientSize.Height);
        _selection = Rectangle.FromLTRB(
            Math.Min(_start.X, x),
            Math.Min(_start.Y, y),
            Math.Max(_start.X, x),
            Math.Max(_start.Y, y));
        Invalidate();
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        if (_selection.Width >= 32 && _selection.Height >= 32)
        {
            ConfirmSelection();
            return;
        }

        _selection = Rectangle.Empty;
        Invalidate();
        base.OnMouseUp(e);
    }

    private void ConfirmSelection()
    {
        int width = _selection.Width - (_selection.Width % 2);
        int height = _selection.Height - (_selection.Height % 2);
        if (width < 16 || height < 16)
        {
            return;
        }

        SelectedRegion = new Rectangle(
            _selection.Left + _virtualScreen.Left,
            _selection.Top + _virtualScreen.Top,
            width,
            height);
        Confirmed = true;
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (_screenshot != null)
        {
            g.DrawImageUnscaled(_screenshot, 0, 0);
        }
        else
        {
            g.Clear(Color.FromArgb(32, 32, 32));
        }

        using (SolidBrush dim = new(Color.FromArgb(145, 0, 0, 0)))
        {
            g.FillRectangle(dim, ClientRectangle);
        }

        if (_selection.Width > 0 && _selection.Height > 0)
        {
            if (_screenshot != null)
            {
                g.DrawImage(
                    _screenshot,
                    _selection,
                    _selection,
                    GraphicsUnit.Pixel);
            }

            using Pen border = new(Color.FromArgb(255, 77, 79), 2);
            g.DrawRectangle(
                border,
                _selection.Left,
                _selection.Top,
                _selection.Width - 1,
                _selection.Height - 1);

            string sizeText = $"{_selection.Width} × {_selection.Height}";
            Size textSize = TextRenderer.MeasureText(sizeText, _font);
            int labelX = Math.Max(4, _selection.Left);
            int labelY = _selection.Top - textSize.Height - 12;
            if (labelY < 4)
            {
                labelY = _selection.Top + 8;
            }

            Rectangle labelRect = new(labelX, labelY, textSize.Width + 20, textSize.Height + 10);
            using (GraphicsPath path = ToolButton.RoundedRect(labelRect, 6))
            using (SolidBrush brush = new(Color.FromArgb(220, 20, 20, 20)))
            {
                g.FillPath(brush, path);
            }

            TextRenderer.DrawText(
                g,
                sizeText,
                _font,
                labelRect,
                Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        string hint = "拖动鼠标框选要录制的区域，松开即可 · 按 Esc 取消";
        Size hintSize = TextRenderer.MeasureText(hint, _hintFont);
        int hintWidth = hintSize.Width + 40;
        Rectangle hintRect = new(
            Math.Max(10, (ClientSize.Width - hintWidth) / 2),
            16,
            hintWidth,
            hintSize.Height + 16);
        using (GraphicsPath path = ToolButton.RoundedRect(hintRect, 10))
        using (SolidBrush brush = new(Color.FromArgb(225, 18, 18, 18)))
        {
            g.FillPath(brush, path);
        }

        using (Pen border = new(Color.FromArgb(60, 60, 60)))
        using (GraphicsPath path = ToolButton.RoundedRect(hintRect, 10))
        {
            g.DrawPath(border, path);
        }

        TextRenderer.DrawText(
            g,
            hint,
            _hintFont,
            hintRect,
            Color.FromArgb(235, 235, 235),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _screenshot?.Dispose();
            _font.Dispose();
            _hintFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
