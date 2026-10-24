using System.Drawing.Drawing2D;

namespace EasyRecorder;

public sealed class RegionBorderForm : Form
{
    private const int Thickness = 2;

    public RegionBorderForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(255, 77, 79);
        ShowIcon = false;
        Enabled = false;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
    }

    public void ShowAround(Rectangle screenRect)
    {
        if (screenRect.Width < 16 || screenRect.Height < 16)
        {
            Hide();
            return;
        }

        Bounds = Rectangle.Inflate(screenRect, Thickness, Thickness);
        using GraphicsPath path = new() { FillMode = FillMode.Alternate };
        path.AddRectangle(new Rectangle(0, 0, Width, Height));
        path.AddRectangle(new Rectangle(Thickness, Thickness, Width - Thickness * 2, Height - Thickness * 2));
        Region? old = Region;
        Region = new Region(path);
        old?.Dispose();
        NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        if (!Visible)
        {
            Show();
        }
        else
        {
            Invalidate();
        }
    }
}
