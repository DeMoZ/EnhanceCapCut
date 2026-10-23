using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using PixelFormat = Vortice.DCommon.PixelFormat;
using AlphaMode = Vortice.DCommon.AlphaMode;

namespace EnhanceCapCut.Windows;

/// <summary>
/// The GPU objects of one export: a Direct3D 11 device (hardware, or WARP when there is no usable GPU), a Direct2D
/// device context on it, and the DirectWrite and WIC factories. Direct2D works in pixels (96 DPI).
/// </summary>
public sealed class Graphics : IDisposable
{
    public ID3D11Device Device3D { get; }
    public ID2D1Factory1 Factory { get; }
    public ID2D1Device Device { get; }
    public ID2D1DeviceContext Context { get; }
    public IDWriteFactory5 Write { get; }
    public IWICImagingFactory Imaging { get; }
    /// <summary>True when Direct3D fell back to the WARP software rasterizer.</summary>
    public bool IsSoftware { get; }

    static readonly FeatureLevel[] Levels =
        [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];

    public Graphics()
    {
        ID3D11Device? device;
        if (D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, Levels, out device).Failure
            || device is null)
        {
            D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport, Levels, out device).CheckError();
            IsSoftware = true;
        }
        Device3D = device!;
        using var dxgi = Device3D.QueryInterface<IDXGIDevice>();
        Factory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        Device = Factory.CreateDevice(dxgi);
        Context = Device.CreateDeviceContext(DeviceContextOptions.None);
        Write = DWrite.DWriteCreateFactory<IDWriteFactory5>();
        Imaging = new IWICImagingFactory();
    }

    static readonly PixelFormat Bgra = new(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied);

    /// <summary>A premultiplied BGRA bitmap; <c>Target</c> lets it be drawn into, <c>CpuRead | CannotDraw</c> read back.</summary>
    public ID2D1Bitmap1 CreateBitmap(int width, int height, BitmapOptions options = BitmapOptions.None) =>
        Context.CreateBitmap(new SizeI(Math.Max(width, 1), Math.Max(height, 1)), IntPtr.Zero, 0,
            new BitmapProperties1(Bgra, 96, 96, options));

    /// <summary>Runs <paramref name="draw"/> with <paramref name="target"/> as the render target.</summary>
    public void DrawInto(ID2D1Bitmap1 target, Action<ID2D1DeviceContext> draw)
    {
        Context.Target = target;
        Context.BeginDraw();
        try
        {
            Context.Transform = System.Numerics.Matrix3x2.Identity;
            draw(Context);
        }
        finally
        {
            Context.EndDraw().CheckError();
            Context.Target = null;
        }
    }

    public void Dispose()
    {
        Imaging.Dispose();
        Write.Dispose();
        Context.Dispose();
        Device.Dispose();
        Factory.Dispose();
        Device3D.Dispose();
    }
}
