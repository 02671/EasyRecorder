using System.Drawing;
using System.Drawing.Imaging;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace EasyRecorder;

internal sealed class DxgiScreenCapture : IDisposable
{
    private const int WaitTimeout = unchecked((int)0x887A0027);
    private const int AccessLost = unchecked((int)0x887A0026);

    private readonly Rectangle _outputBounds;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;
    private bool _hasFrame;
    private bool _disposed;

    private DxgiScreenCapture(
        ID3D11Device device,
        ID3D11DeviceContext context,
        IDXGIOutputDuplication duplication,
        ID3D11Texture2D staging,
        Rectangle outputBounds)
    {
        _device = device;
        _context = context;
        _duplication = duplication;
        _staging = staging;
        _outputBounds = outputBounds;
    }

    public static DxgiScreenCapture? TryCreate(Rectangle region)
    {
        try
        {
            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1 adapter).Success; adapterIndex++)
            {
                using (adapter)
                {
                    for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out IDXGIOutput output).Success; outputIndex++)
                    {
                        using (output)
                        {
                            OutputDescription description = output.Description;
                            var desktop = description.DesktopCoordinates;
                            Rectangle bounds = Rectangle.FromLTRB(desktop.Left, desktop.Top, desktop.Right, desktop.Bottom);
                            if (bounds.Width < 16 || bounds.Height < 16)
                            {
                                continue;
                            }

                            if (region.Left < bounds.Left ||
                                region.Top < bounds.Top ||
                                region.Right > bounds.Right ||
                                region.Bottom > bounds.Bottom)
                            {
                                continue;
                            }

                            DxgiScreenCapture? capture = TryCreateOnOutput(adapter, output, bounds);
                            if (capture != null)
                            {
                                return capture;
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static DxgiScreenCapture? TryCreateOnOutput(IDXGIAdapter1 adapter, IDXGIOutput output, Rectangle bounds)
    {
        FeatureLevel[] levels =
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0
        };

        Result created = D3D11.D3D11CreateDevice(
            adapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            levels,
            out ID3D11Device? device,
            out _,
            out ID3D11DeviceContext? context);

        if (created.Failure || device == null || context == null)
        {
            device?.Dispose();
            context?.Dispose();
            return null;
        }

        try
        {
            using IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
            IDXGIOutputDuplication duplication = output1.DuplicateOutput(device);
            Texture2DDescription stagingDescription = new()
            {
                Width = (uint)bounds.Width,
                Height = (uint)bounds.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            };

            ID3D11Texture2D staging = device.CreateTexture2D(stagingDescription);
            return new DxgiScreenCapture(device, context, duplication, staging, bounds);
        }
        catch
        {
            device.Dispose();
            context.Dispose();
            return null;
        }
    }

    public bool TryCapture(Bitmap destination, Rectangle region)
    {
        if (_disposed || _duplication == null || _context == null || _staging == null)
        {
            return false;
        }

        Result acquired = _duplication.AcquireNextFrame(8, out _, out IDXGIResource? resource);
        if (acquired.Failure)
        {
            if (acquired.Code == AccessLost)
            {
                Dispose();
                return false;
            }

            return acquired.Code == WaitTimeout && _hasFrame;
        }

        try
        {
            if (resource == null)
            {
                return false;
            }

            using ID3D11Texture2D source = resource.QueryInterface<ID3D11Texture2D>();
            int left = Math.Max(0, region.Left - _outputBounds.Left);
            int top = Math.Max(0, region.Top - _outputBounds.Top);
            int right = Math.Min(_outputBounds.Width, left + region.Width);
            int bottom = Math.Min(_outputBounds.Height, top + region.Height);
            if (right - left < 16 || bottom - top < 16)
            {
                return false;
            }

            _context.CopySubresourceRegion(
                _staging,
                0,
                0,
                0,
                0,
                source,
                0,
                new Box(left, top, 0, right, bottom, 1));

            MappedSubresource mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                CopyMapped(mapped, destination, region.Width, region.Height);
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }

            _hasFrame = true;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            resource?.Dispose();
            try
            {
                _duplication.ReleaseFrame();
            }
            catch
            {
            }
        }
    }

    public bool IsAvailable => !_disposed;

    private static unsafe void CopyMapped(MappedSubresource mapped, Bitmap destination, int width, int height)
    {
        BitmapData data = destination.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            int copyBytes = width * 4;
            byte* source = (byte*)mapped.DataPointer;
            byte* dest = (byte*)data.Scan0;
            for (int y = 0; y < height; y++)
            {
                Buffer.MemoryCopy(source, dest, copyBytes, copyBytes);
                source += mapped.RowPitch;
                dest += data.Stride;
            }
        }
        finally
        {
            destination.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { _duplication?.Dispose(); } catch { }
        try { _staging?.Dispose(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        _duplication = null;
        _staging = null;
        _context = null;
        _device = null;
    }
}
