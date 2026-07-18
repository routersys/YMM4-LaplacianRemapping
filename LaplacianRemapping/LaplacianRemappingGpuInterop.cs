using System.Runtime.InteropServices;
using ComputeSharp;
using ComputeSharp.Interop;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using YukkuriMovieMaker.Commons;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace LaplacianRemapping;

internal sealed class LaplacianRemappingGpuInterop : IDisposable
{
    private static readonly Guid D3D12FenceGuid = new("0a753dcf-c4d8-4b91-adf6-be5a60d95a76");

    private readonly GraphicsDevice _device;
    private readonly ID3D11Device1 _d3dDevice;
    private readonly ID3D11DeviceContext4 _d3dContext;
    private readonly ID3D11Fence _fence;
    private readonly ID2D1DeviceContext6 _renderContext;
    private nint _d3d12Fence;
    private ReadWriteTexture2D<Bgra32, Float4>? _sourceTexture;
    private ReadWriteTexture2D<Bgra32, Float4>? _outputTexture;
    private ID3D11Texture2D? _sourceD3D11Texture;
    private ID3D11Texture2D? _outputD3D11Texture;
    private ID2D1Bitmap1? _sourceBitmap;
    private ID2D1Bitmap1? _outputBitmap;
    private int _sourceWidth;
    private int _sourceHeight;
    private int _outputWidth;
    private int _outputHeight;
    private ulong _fenceValue;
    private bool _computeActive;
    private bool _disposed;

    private LaplacianRemappingGpuInterop(
        GraphicsDevice device,
        ID3D11Device1 d3dDevice,
        ID3D11DeviceContext4 d3dContext,
        ID3D11Fence fence,
        ID2D1DeviceContext6 renderContext,
        nint d3d12Fence)
    {
        _device = device;
        _d3dDevice = d3dDevice;
        _d3dContext = d3dContext;
        _fence = fence;
        _renderContext = renderContext;
        _d3d12Fence = d3d12Fence;
    }

    public GraphicsDevice Device => _device;

    public ReadWriteTexture2D<Bgra32, Float4> SourceTexture => _sourceTexture!;

    public ReadWriteTexture2D<Bgra32, Float4> OutputTexture => _outputTexture!;

    public ID2D1Bitmap1 OutputBitmap => _outputBitmap!;

    public bool SourceMatches(int width, int height)
        => _sourceTexture is not null && _sourceWidth == width && _sourceHeight == height;

    public bool OutputCovers(int width, int height)
        => _outputTexture is not null && _outputWidth >= width && _outputHeight >= height;

    public static unsafe LaplacianRemappingGpuInterop? TryCreate(IGraphicsDevicesAndContext devices)
    {
        ID3D11Device1? d3dDevice = null;
        ID3D11DeviceContext4? d3dContext = null;
        ID3D11Fence? fence = null;
        ID2D1DeviceContext6? renderContext = null;
        nint d3d12Fence = 0;
        nint fenceHandle = 0;
        try
        {
            var device = GetMatchingDevice(devices);
            d3dDevice = devices.D3D.Device.QueryInterface<ID3D11Device1>();
            d3dContext = devices.D3D.DeviceContext.QueryInterface<ID3D11DeviceContext4>();
            using var d3dDevice5 = devices.D3D.Device.QueryInterface<ID3D11Device5>();
            var fenceGuid = D3D12FenceGuid;
            InteropServices.CreateSharedFence(device, &fenceGuid, (void**)&d3d12Fence, &fenceHandle);
            fence = d3dDevice5.OpenSharedFence(fenceHandle);
            renderContext = devices.D2D.Device.CreateDeviceContext(DeviceContextOptions.EnableMultithreadedOptimizations);
            return new LaplacianRemappingGpuInterop(device, d3dDevice, d3dContext, fence, renderContext, d3d12Fence);
        }
        catch
        {
            renderContext?.Dispose();
            fence?.Dispose();
            d3dContext?.Dispose();
            d3dDevice?.Dispose();
            if (d3d12Fence != 0)
                Marshal.Release(d3d12Fence);
            return null;
        }
        finally
        {
            if (fenceHandle != 0)
                CloseHandle(fenceHandle);
        }
    }

    public bool EnsureResources(int width, int height)
    {
        var sourceChanged = EnsureSource(width, height);
        var outputChanged = EnsureOutput(width, height);
        return sourceChanged || outputChanged;
    }

    public bool EnsureSource(int width, int height)
    {
        if (SourceMatches(width, height))
            return false;

        if (_sourceTexture is not null)
            WaitForIdle();
        ReleaseSourceResources();
        CreateSourceResources(width, height);
        return true;
    }

    public bool EnsureOutput(int width, int height)
    {
        if (OutputCovers(width, height))
            return false;

        var capacityWidth = Math.Max(width, _outputWidth);
        var capacityHeight = Math.Max(height, _outputHeight);
        if (_outputTexture is not null)
            WaitForIdle();
        ReleaseOutputResources();
        CreateOutputResources(capacityWidth, capacityHeight);
        return true;
    }

    public void RenderInput(ID2D1Image source, RawRectF bounds)
    {
        _renderContext.BeginDraw();
        _renderContext.Clear(null);
        _renderContext.DrawImage(
            source,
            new System.Numerics.Vector2(-bounds.Left, -bounds.Top),
            null,
            InterpolationMode.NearestNeighbor,
            CompositeMode.SourceCopy);
        _renderContext.EndDraw();
    }

    public unsafe void BeginCompute()
    {
        if (_computeActive)
            throw new InvalidOperationException();
        var value = ++_fenceValue;
        _d3dContext.Signal(_fence, value);
        _d3dContext.Flush();
        InteropServices.WaitForSharedFence(_device, (void*)_d3d12Fence, value);
        _computeActive = true;
    }

    public unsafe void EndCompute()
    {
        if (!_computeActive)
            throw new InvalidOperationException();
        try
        {
            var value = ++_fenceValue;
            InteropServices.SignalSharedFence(_device, (void*)_d3d12Fence, value);
            _d3dContext.Wait(_fence, value);
        }
        finally
        {
            _computeActive = false;
        }
    }

    public void WaitForIdle()
    {
        if (_computeActive)
            throw new InvalidOperationException();
        var value = ++_fenceValue;
        _d3dContext.Signal(_fence, value);
        _d3dContext.Flush();
        _fence.SetEventOnCompletion(value, 0);
    }

    private static GraphicsDevice GetMatchingDevice(IGraphicsDevicesAndContext devices)
    {
        var adapterLuid = devices.DXGI.Adapter.Description.Luid.ToString();
        using var enumerator = GraphicsDevice.QueryDevices(
            device => string.Equals(device.Luid.ToString(), adapterLuid, StringComparison.Ordinal)).GetEnumerator();
        if (!enumerator.MoveNext())
            throw new NotSupportedException();
        return enumerator.Current;
    }

    private void CreateSourceResources(int width, int height)
    {
        ReadWriteTexture2D<Bgra32, Float4>? sourceTexture = null;
        ID3D11Texture2D? sourceD3D11Texture = null;
        ID2D1Bitmap1? sourceBitmap = null;
        nint sourceHandle = 0;
        try
        {
            sourceTexture = InteropServices.AllocateSharedReadWriteTexture2D<Bgra32, Float4>(_device, width, height);
            sourceHandle = InteropServices.CreateSharedHandle(sourceTexture);
            sourceD3D11Texture = _d3dDevice.OpenSharedResource1<ID3D11Texture2D>(sourceHandle);
            using var sourceSurface = sourceD3D11Texture.QueryInterface<IDXGISurface>();
            var pixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
            sourceBitmap = _renderContext.CreateBitmapFromDxgiSurface(
                sourceSurface,
                new BitmapProperties1(pixelFormat, 96f, 96f, BitmapOptions.Target));
            _renderContext.Target = sourceBitmap;

            _sourceTexture = sourceTexture;
            _sourceD3D11Texture = sourceD3D11Texture;
            _sourceBitmap = sourceBitmap;
            _sourceWidth = width;
            _sourceHeight = height;
            sourceTexture = null;
            sourceD3D11Texture = null;
            sourceBitmap = null;
        }
        finally
        {
            if (sourceHandle != 0)
                CloseHandle(sourceHandle);
            sourceBitmap?.Dispose();
            sourceD3D11Texture?.Dispose();
            sourceTexture?.Dispose();
        }
    }

    private void CreateOutputResources(int width, int height)
    {
        ReadWriteTexture2D<Bgra32, Float4>? outputTexture = null;
        ID3D11Texture2D? outputD3D11Texture = null;
        ID2D1Bitmap1? outputBitmap = null;
        nint outputHandle = 0;
        try
        {
            outputTexture = InteropServices.AllocateSharedReadWriteTexture2D<Bgra32, Float4>(_device, width, height);
            outputHandle = InteropServices.CreateSharedHandle(outputTexture);
            outputD3D11Texture = _d3dDevice.OpenSharedResource1<ID3D11Texture2D>(outputHandle);
            using var outputSurface = outputD3D11Texture.QueryInterface<IDXGISurface>();
            var pixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
            outputBitmap = _renderContext.CreateBitmapFromDxgiSurface(
                outputSurface,
                new BitmapProperties1(pixelFormat, 96f, 96f, BitmapOptions.None));

            _outputTexture = outputTexture;
            _outputD3D11Texture = outputD3D11Texture;
            _outputBitmap = outputBitmap;
            _outputWidth = width;
            _outputHeight = height;
            outputTexture = null;
            outputD3D11Texture = null;
            outputBitmap = null;
        }
        finally
        {
            if (outputHandle != 0)
                CloseHandle(outputHandle);
            outputBitmap?.Dispose();
            outputD3D11Texture?.Dispose();
            outputTexture?.Dispose();
        }
    }

    private void ReleaseSourceResources()
    {
        _renderContext.Target = null;
        _sourceBitmap?.Dispose();
        _sourceD3D11Texture?.Dispose();
        _sourceTexture?.Dispose();
        _sourceBitmap = null;
        _sourceD3D11Texture = null;
        _sourceTexture = null;
        _sourceWidth = 0;
        _sourceHeight = 0;
    }

    private void ReleaseOutputResources()
    {
        _outputBitmap?.Dispose();
        _outputD3D11Texture?.Dispose();
        _outputTexture?.Dispose();
        _outputBitmap = null;
        _outputD3D11Texture = null;
        _outputTexture = null;
        _outputWidth = 0;
        _outputHeight = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        try
        {
            WaitForIdle();
        }
        finally
        {
            _disposed = true;
            ReleaseOutputResources();
            ReleaseSourceResources();
            _renderContext.Dispose();
            _fence.Dispose();
            _d3dContext.Dispose();
            _d3dDevice.Dispose();
            if (_d3d12Fence != 0)
            {
                Marshal.Release(_d3d12Fence);
                _d3d12Fence = 0;
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
