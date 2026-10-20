using System.Drawing.Drawing2D;

namespace EasyRecorder;

public sealed class ToolButton : Control
{
    private bool _hover;
    private bool _pressed;

    public bool Accent { get; set; }
    public bool Danger { get; set; }

    public ToolButton(string text)
    {
        Text = text;
        Cursor = Cursors.Hand;
        Font = UiFonts.Create(8.5f);
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color back;
        Color fore;

        if (!Enabled)
        {
            back = Color.FromArgb(30, 30, 30);
            fore = Color.FromArgb(105, 105, 105);
        }
        else if (Accent)
        {
            back = _pressed
                ? Color.FromArgb(170, 27, 18)
                : _hover ? Color.FromArgb(226, 60, 45) : Color.FromArgb(196, 43, 28);
            fore = Color.White;
        }
        else if (Danger && _hover)
        {
            back = _pressed ? Color.FromArgb(150, 30, 22) : Color.FromArgb(196, 43, 28);
            fore = Color.White;
        }
        else
        {
            back = _pressed
                ? Color.FromArgb(64, 64, 64)
                : _hover ? Color.FromArgb(52, 52, 52) : Color.FromArgb(38, 38, 38);
            fore = Color.FromArgb(235, 235, 235);
        }

        Rectangle rect = new(0, 0, Width - 1, Height - 1);
        using GraphicsPath path = RoundedRect(rect, Math.Max(4, Height / 7));
        using SolidBrush brush = new(back);
        g.FillPath(brush, path);

        TextRenderer.DrawText(
            g,
            Text,
            Font,
            rect,
            fore,
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix);
    }

    public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
        GraphicsPath path = new();
        if (radius <= 0)
        {
            path.AddRectangle(bounds);
            path.CloseFigure();
            return path;
        }

        Rectangle arc = new(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}
