using System.Runtime.InteropServices;
using ComputeSharp;
using ComputeSharp.Interop;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace LaplacianRemapping.Tests;

public sealed class LaplacianRemappingEffectTests
{
    private static double ValueAt(YukkuriMovieMaker.Commons.Animation animation) => animation.GetValue(0, 1, 30);

    private static LaplacianRemappingPipeline.Parameters CreateParameters(
        LaplacianRemappingQuality quality = LaplacianRemappingQuality.Balanced,
        float detail = 0.5f,
        float tone = 0f,
        float threshold = 0.3f)
        => new(quality, detail, tone, threshold);

    [Fact]
    public void DefaultParameterValuesMatchSpecification()
    {
        var effect = new LaplacianRemappingEffect();

        Assert.Equal(100d, ValueAt(effect.Amount), 6);
        Assert.Equal(50d, ValueAt(effect.Detail), 6);
        Assert.Equal(0d, ValueAt(effect.Tone), 6);
        Assert.Equal(30d, ValueAt(effect.Threshold), 6);
        Assert.Equal(LaplacianRemappingQuality.High, effect.Quality);
    }

    [Fact]
    public void CreateExoVideoFiltersReturnsEmpty()
    {
        var effect = new LaplacianRemappingEffect();

        Assert.Empty(effect.CreateExoVideoFilters(0, null!));
    }

    [Theory]
    [InlineData(LaplacianRemappingQuality.Balanced, 7)]
    [InlineData(LaplacianRemappingQuality.High, 11)]
    [InlineData(LaplacianRemappingQuality.Ultra, 15)]
    public void QualitySettingsMatchSpecification(LaplacianRemappingQuality quality, int sampleCount)
    {
        var settings = LaplacianRemappingSettings.GetQuality(quality);

        Assert.Equal(sampleCount, settings.SampleCount);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(3, 3, 2)]
    [InlineData(8, 8, 3)]
    [InlineData(1920, 1080, 11)]
    [InlineData(4096, 4096, 12)]
    [InlineData(8192, 8192, 13)]
    public void PyramidDepthReachesMinimumTopSize(int width, int height, int expected)
    {
        Assert.Equal(expected, LaplacianRemappingSettings.GetPyramidDepth(width, height));
    }

    [Fact]
    public void ParameterMappingsAreMonotonicAndBounded()
    {
        Assert.Equal(1f, LaplacianRemappingSettings.GetDetailAlpha(0f), 6);
        Assert.Equal(LaplacianRemappingSettings.MinimumDetailAlpha, LaplacianRemappingSettings.GetDetailAlpha(1f), 6);
        Assert.Equal(LaplacianRemappingSettings.MaximumDetailAlpha, LaplacianRemappingSettings.GetDetailAlpha(-1f), 6);
        Assert.Equal(LaplacianRemappingSettings.MinimumDetailAlpha, LaplacianRemappingSettings.GetDetailAlpha(5f), 6);
        Assert.Equal(LaplacianRemappingSettings.MaximumDetailAlpha, LaplacianRemappingSettings.GetDetailAlpha(-5f), 6);
        Assert.True(LaplacianRemappingSettings.GetDetailAlpha(0.75f) < LaplacianRemappingSettings.GetDetailAlpha(0.25f));
        Assert.True(LaplacianRemappingSettings.GetDetailAlpha(-0.75f) > LaplacianRemappingSettings.GetDetailAlpha(-0.25f));

        Assert.Equal(1f, LaplacianRemappingSettings.GetToneBeta(0f), 6);
        Assert.Equal(0f, LaplacianRemappingSettings.GetToneBeta(-1f), 6);
        Assert.Equal(LaplacianRemappingSettings.MaximumToneBeta, LaplacianRemappingSettings.GetToneBeta(1f), 6);
        Assert.Equal(0f, LaplacianRemappingSettings.GetToneBeta(-5f), 6);
        Assert.Equal(LaplacianRemappingSettings.MaximumToneBeta, LaplacianRemappingSettings.GetToneBeta(5f), 6);
        Assert.True(LaplacianRemappingSettings.GetToneBeta(0.75f) > LaplacianRemappingSettings.GetToneBeta(0.25f));

        Assert.Equal(LaplacianRemappingSettings.MinimumSigma, LaplacianRemappingSettings.GetSigma(0f), 6);
        Assert.Equal(LaplacianRemappingSettings.MaximumSigma, LaplacianRemappingSettings.GetSigma(5f), 6);
        Assert.Equal(0.3f, LaplacianRemappingSettings.GetSigma(0.3f), 6);
    }

    [Fact]
    public void TransparentInputYieldsTransparentOutput()
    {
        using var pipeline = LaplacianRemappingPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 64;
        const int height = 64;
        var source = new int[width * height];
        var destination = new int[source.Length];
        Array.Fill(destination, -1);
        var parameters = CreateParameters();

        pipeline.Process(source, destination, width, height, in parameters);

        Assert.All(destination, pixel => Assert.Equal(0, pixel));
    }

    [Fact]
    public void IdentityParametersPreserveImage()
    {
        using var pipeline = LaplacianRemappingPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 128;
        const int height = 128;
        var source = CreateTexturedSource(width, height);
        var destination = new int[source.Length];
        var parameters = CreateParameters(detail: 0f, tone: 0f);

        pipeline.Process(source, destination, width, height, in parameters);

        for (var index = 0; index < source.Length; index++)
        {
            var expected = source[index];
            var actual = destination[index];
            for (var shift = 0; shift < 32; shift += 8)
            {
                var e = (expected >> shift) & 255;
                var a = (actual >> shift) & 255;
                Assert.InRange(a, e - 1, e + 1);
            }
        }
    }

    [Fact]
    public void GpuPipelineIsDeterministic()
    {
        using var pipeline = LaplacianRemappingPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 128;
        const int height = 128;
        var source = CreateTexturedSource(width, height);
        var first = new int[source.Length];
        var second = new int[source.Length];
        var parameters = CreateParameters(detail: 0.8f, tone: -0.5f);

        pipeline.Process(source, first, width, height, in parameters);
        pipeline.Process(source, second, width, height, in parameters);

        Assert.Equal(first, second);
    }

    [Fact]
    public void DifferentDetailProducesDifferentOutput()
    {
        using var pipeline = LaplacianRemappingPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 128;
        const int height = 128;
        var source = CreateTexturedSource(width, height);
        var first = new int[source.Length];
        var second = new int[source.Length];

        var parametersA = CreateParameters(detail: 1f);
        var parametersB = CreateParameters(detail: -1f);
        pipeline.Process(source, first, width, height, in parametersA);
        pipeline.Process(source, second, width, height, in parametersB);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void OutputAlphaMatchesInputAndStaysPremultiplied()
    {
        using var pipeline = LaplacianRemappingPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 128;
        const int height = 128;
        var source = CreateTexturedSource(width, height);
        var destination = new int[source.Length];
        var parameters = CreateParameters(detail: 1f, tone: 1f);

        pipeline.Process(source, destination, width, height, in parameters);

        for (var index = 0; index < source.Length; index++)
        {
            var pixel = destination[index];
            var alpha = (pixel >> 24) & 255;
            Assert.Equal((source[index] >> 24) & 255, alpha);
            Assert.InRange((pixel >> 16) & 255, 0, alpha);
            Assert.InRange((pixel >> 8) & 255, 0, alpha);
            Assert.InRange(pixel & 255, 0, alpha);
        }
    }

    [Fact]
    public void DetailControlsLocalVariance()
    {
        using var pipeline = LaplacianRemappingPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 128;
        const int height = 128;
        var source = CreateTexturedSource(width, height);
        var identity = new int[source.Length];
        var enhanced = new int[source.Length];
        var smoothed = new int[source.Length];

        pipeline.Process(source, identity, width, height, CreateParameters(detail: 0f));
        pipeline.Process(source, enhanced, width, height, CreateParameters(detail: 1f));
        pipeline.Process(source, smoothed, width, height, CreateParameters(detail: -1f));

        var baseVariance = InteriorLuminanceVariance(identity, width, height);
        Assert.True(InteriorLuminanceVariance(enhanced, width, height) > baseVariance);
        Assert.True(InteriorLuminanceVariance(smoothed, width, height) < baseVariance);
    }

    [Fact]
    public void ToneCompressionReducesEdgeAmplitude()
    {
        using var pipeline = LaplacianRemappingPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 128;
        const int height = 128;
        var source = CreateStepSource(width, height);
        var identity = new int[source.Length];
        var compressed = new int[source.Length];

        pipeline.Process(source, identity, width, height, CreateParameters(detail: 0f, tone: 0f));
        pipeline.Process(source, compressed, width, height, CreateParameters(detail: 0f, tone: -1f));

        var baseAmplitude = HalfMeanDifference(identity, width, height);
        var compressedAmplitude = HalfMeanDifference(compressed, width, height);
        Assert.True(baseAmplitude > 0d);
        Assert.True(compressedAmplitude < baseAmplitude);
    }

    [Fact]
    public void GpuPipelineDoesNotAllocateManagedMemoryAfterWarmup()
    {
        using var pipeline = LaplacianRemappingPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 64;
        const int height = 64;
        var source = CreateTexturedSource(width, height);
        var destination = new int[source.Length];
        var parameters = CreateParameters();
        pipeline.Process(source, destination, width, height, in parameters);
        pipeline.Process(source, destination, width, height, in parameters);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        pipeline.Process(source, destination, width, height, in parameters);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void SharedTexturePipelineMatchesPackedBufferPipeline()
    {
        using var pipeline = LaplacianRemappingPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 96;
        const int height = 96;
        var source = CreateTexturedSource(width, height);
        var expected = new int[source.Length];
        var parameters = CreateParameters(detail: 0.7f, tone: -0.3f);
        pipeline.Process(source, expected, width, height, in parameters);

        var device = GraphicsDevice.GetDefault();
        using var sourceTexture = InteropServices.AllocateSharedReadWriteTexture2D<Bgra32, Float4>(device, width, height);
        using var outputTexture = InteropServices.AllocateSharedReadWriteTexture2D<Bgra32, Float4>(device, width, height);
        var sourcePixels = new Bgra32[source.Length];
        for (var index = 0; index < source.Length; index++)
            sourcePixels[index].PackedValue = unchecked((uint)source[index]);
        sourceTexture.CopyFrom(sourcePixels);
        pipeline.ProcessSharedAndWait(sourceTexture, outputTexture, width, height, in parameters);
        var result = new Bgra32[source.Length];
        outputTexture.CopyTo(result);

        for (var index = 0; index < expected.Length; index++)
            Assert.Equal(unchecked((uint)expected[index]), result[index].PackedValue);
    }

    [Fact]
    public void SubmittedSharedTexturePipelineAllocationsAmortizeToZero()
    {
        using var pipeline = LaplacianRemappingPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 64;
        const int height = 64;
        var device = GraphicsDevice.GetDefault();
        using var source = InteropServices.AllocateSharedReadWriteTexture2D<Bgra32, Float4>(device, width, height);
        using var destination = InteropServices.AllocateSharedReadWriteTexture2D<Bgra32, Float4>(device, width, height);
        var parameters = CreateParameters();
        for (var iteration = 0; iteration < 4; iteration++)
            pipeline.Process(source, destination, width, height, in parameters);
        pipeline.WaitForCompletion();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var minimum = long.MaxValue;
        for (var iteration = 0; iteration < 16; iteration++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            pipeline.Process(source, destination, width, height, in parameters);
            minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        pipeline.WaitForCompletion();

        Assert.Equal(0, minimum);
    }

    [Fact]
    public void VisibleBoundsCoverAllLitPixelsAndMatchFullRender()
    {
        using var pipeline = LaplacianRemappingPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 192;
        const int height = 192;
        var source = CreateOffsetSource(width, height, 40, 56, 72, 48);
        var full = new int[source.Length];
        var parameters = CreateParameters(detail: 0.8f, tone: -0.4f);
        pipeline.Process(source, full, width, height, in parameters);

        var device = GraphicsDevice.GetDefault();
        using var sourceTexture = device.AllocateReadWriteTexture2D<Bgra32, Float4>(width, height);
        var sourcePixels = new Bgra32[source.Length];
        for (var index = 0; index < source.Length; index++)
            sourcePixels[index].PackedValue = unchecked((uint)source[index]);
        sourceTexture.CopyFrom(sourcePixels);

        pipeline.Simulate(sourceTexture, width, height, in parameters);
        Assert.True(pipeline.TryGetVisibleBounds(width, height, out var rect));
        Assert.True(rect.Width > 0 && rect.Height > 0);
        Assert.True(rect.X >= 0 && rect.Y >= 0);
        Assert.True(rect.X + rect.Width <= width && rect.Y + rect.Height <= height);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (((source[y * width + x] >> 24) & 255) == 0)
                    continue;
                Assert.InRange(x, rect.X, rect.X + rect.Width - 1);
                Assert.InRange(y, rect.Y, rect.Y + rect.Height - 1);
            }
        }

        using var outputTexture = device.AllocateReadWriteTexture2D<Bgra32, Float4>(rect.Width, rect.Height);
        pipeline.RenderVisible(sourceTexture, outputTexture, width, height, rect);
        var result = new Bgra32[rect.Width * rect.Height];
        outputTexture.CopyTo(result);

        for (var y = 0; y < rect.Height; y++)
        {
            for (var x = 0; x < rect.Width; x++)
            {
                var expected = unchecked((uint)full[(rect.Y + y) * width + rect.X + x]);
                Assert.Equal(expected, result[y * rect.Width + x].PackedValue);
            }
        }
    }

    [Fact]
    public void SimulatePathMatchesFullRenderOnNarrowLuminanceRange()
    {
        using var pipeline = LaplacianRemappingPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 128;
        const int height = 128;
        var source = new int[width * height];
        for (var y = 8; y < height - 8; y++)
        {
            for (var x = 8; x < width - 8; x++)
            {
                var value = 24 + (((x / 4 + y / 4) & 1) == 0 ? 24 : 0) + 16 * x / width;
                source[y * width + x] = unchecked((int)0xFF000000) | value << 16 | value << 8 | value;
            }
        }
        var full = new int[source.Length];
        var parameters = CreateParameters(detail: 1f, tone: -0.5f, threshold: 0.1f);
        pipeline.Process(source, full, width, height, in parameters);

        var device = GraphicsDevice.GetDefault();
        using var sourceTexture = device.AllocateReadWriteTexture2D<Bgra32, Float4>(width, height);
        var sourcePixels = new Bgra32[source.Length];
        for (var index = 0; index < source.Length; index++)
            sourcePixels[index].PackedValue = unchecked((uint)source[index]);
        sourceTexture.CopyFrom(sourcePixels);

        pipeline.Simulate(sourceTexture, width, height, in parameters);
        Assert.True(pipeline.TryGetVisibleBounds(width, height, out var rect));
        using var outputTexture = device.AllocateReadWriteTexture2D<Bgra32, Float4>(rect.Width, rect.Height);
        pipeline.RenderVisible(sourceTexture, outputTexture, width, height, rect);
        var result = new Bgra32[rect.Width * rect.Height];
        outputTexture.CopyTo(result);

        for (var y = 0; y < rect.Height; y++)
        {
            for (var x = 0; x < rect.Width; x++)
            {
                var expected = unchecked((uint)full[(rect.Y + y) * width + rect.X + x]);
                Assert.Equal(expected, result[y * rect.Width + x].PackedValue);
            }
        }
    }

    [Fact]
    public void SimulateCachesStructureUntilInputsChange()
    {
        using var pipeline = LaplacianRemappingPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 128;
        const int height = 128;
        var source = CreateTexturedSource(width, height);
        var device = GraphicsDevice.GetDefault();
        using var sourceTexture = device.AllocateReadWriteTexture2D<Bgra32, Float4>(width, height);
        var sourcePixels = new Bgra32[source.Length];
        for (var index = 0; index < source.Length; index++)
            sourcePixels[index].PackedValue = unchecked((uint)source[index]);
        sourceTexture.CopyFrom(sourcePixels);

        var parameters = CreateParameters(detail: 0.5f);
        Assert.True(pipeline.Simulate(sourceTexture, width, height, in parameters));
        Assert.False(pipeline.Simulate(sourceTexture, width, height, in parameters));

        var detailChanged = parameters with { Detail = 0.6f };
        Assert.True(pipeline.Simulate(sourceTexture, width, height, in detailChanged));

        var toneChanged = detailChanged with { Tone = -0.5f };
        Assert.True(pipeline.Simulate(sourceTexture, width, height, in toneChanged));
        Assert.False(pipeline.Simulate(sourceTexture, width, height, in toneChanged));

        var movedSource = CreateOffsetSource(width, height, 16, 16, 64, 64);
        for (var index = 0; index < movedSource.Length; index++)
            sourcePixels[index].PackedValue = unchecked((uint)movedSource[index]);
        sourceTexture.CopyFrom(sourcePixels);
        Assert.True(pipeline.Simulate(sourceTexture, width, height, in toneChanged));
    }

    [Fact]
    public void Direct2DInteropProducesFilteredOutput()
    {
        using var devices = new GraphicsDevices();
        using var graphicsContext = devices.CreateContext();
        using var interop = LaplacianRemappingGpuInterop.TryCreate(graphicsContext);
        if (interop is null)
        {
            Assert.Skip("Direct3D 11 and Direct3D 12 sharing is unavailable.");
            return;
        }

        using var pipeline = LaplacianRemappingPipeline.TryCreate(interop.Device);
        Assert.NotNull(pipeline);

        const int width = 96;
        const int height = 96;
        var pixels = CreateTexturedSource(width, height);
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        using var inputBitmap = graphicsContext.DeviceContext.CreateBitmap(
            new SizeI(width, height),
            new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96f,
                96f,
                BitmapOptions.None));
        try
        {
            inputBitmap.CopyFromMemory(handle.AddrOfPinnedObject(), width * sizeof(int));
        }
        finally
        {
            handle.Free();
        }

        Assert.True(interop.EnsureResources(width, height));
        var bounds = new RawRectF(0f, 0f, width, height);
        var parameters = CreateParameters(detail: 1f);
        for (var iteration = 0; iteration < 2; iteration++)
        {
            interop.RenderInput(inputBitmap, bounds);
            interop.BeginCompute();
            try
            {
                pipeline!.Process(interop.SourceTexture, interop.OutputTexture, width, height, in parameters);
            }
            finally
            {
                interop.EndCompute();
            }
        }
        interop.WaitForIdle();

        using var staging = graphicsContext.DeviceContext.CreateBitmap(
            new SizeI(width, height),
            new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96f,
                96f,
                BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        staging.CopyFromBitmap(interop.OutputBitmap);
        var mapped = staging.Map(MapOptions.Read);
        try
        {
            var lit = 0;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var actual = Marshal.ReadInt32(mapped.Bits + (nint)(y * mapped.Pitch + x * sizeof(int)));
                    var alpha = (actual >> 24) & 255;
                    Assert.InRange((actual >> 16) & 255, 0, alpha);
                    Assert.InRange((actual >> 8) & 255, 0, alpha);
                    Assert.InRange(actual & 255, 0, alpha);
                    if (alpha > 0)
                        lit++;
                }
            }
            Assert.True(lit > 0);
        }
        finally
        {
            staging.Unmap();
        }
    }

    private static int[] CreateTexturedSource(int width, int height)
    {
        var source = new int[width * height];
        for (var y = 8; y < height - 8; y++)
        {
            for (var x = 8; x < width - 8; x++)
            {
                var gradient = 96 + 64 * x / width;
                var texture = ((x / 4 + y / 4) & 1) == 0 ? 20 : -20;
                var value = Math.Clamp(gradient + texture, 0, 255);
                source[y * width + x] = unchecked((int)0xFF000000) | value << 16 | value << 8 | value;
            }
        }
        return source;
    }

    private static int[] CreateStepSource(int width, int height)
    {
        var source = new int[width * height];
        for (var y = 8; y < height - 8; y++)
        {
            for (var x = 8; x < width - 8; x++)
            {
                var value = x < width / 2 ? 48 : 208;
                source[y * width + x] = unchecked((int)0xFF000000) | value << 16 | value << 8 | value;
            }
        }
        return source;
    }

    private static int[] CreateOffsetSource(int width, int height, int left, int top, int rectWidth, int rectHeight)
    {
        var source = new int[width * height];
        for (var y = top; y < top + rectHeight; y++)
        {
            for (var x = left; x < left + rectWidth; x++)
            {
                if (x < 0 || x >= width || y < 0 || y >= height)
                    continue;
                var texture = ((x / 4 + y / 4) & 1) == 0 ? 160 : 96;
                source[y * width + x] = unchecked((int)0xFF000000) | texture << 16 | texture << 8 | texture;
            }
        }
        return source;
    }

    private static double InteriorLuminanceVariance(int[] pixels, int width, int height)
    {
        var sum = 0d;
        var squares = 0d;
        var count = 0;
        for (var y = 16; y < height - 16; y++)
        {
            for (var x = 16; x < width - 16; x++)
            {
                var pixel = pixels[y * width + x];
                var luminance = 0.2126 * ((pixel >> 16) & 255) + 0.7152 * ((pixel >> 8) & 255) + 0.0722 * (pixel & 255);
                sum += luminance;
                squares += luminance * luminance;
                count++;
            }
        }
        var mean = sum / count;
        return squares / count - mean * mean;
    }

    private static double HalfMeanDifference(int[] pixels, int width, int height)
    {
        var leftSum = 0d;
        var rightSum = 0d;
        var count = 0;
        for (var y = 16; y < height - 16; y++)
        {
            for (var x = 16; x < width / 2 - 16; x++)
            {
                leftSum += (pixels[y * width + x] >> 8) & 255;
                rightSum += (pixels[y * width + width - 1 - x] >> 8) & 255;
                count++;
            }
        }
        return (rightSum - leftSum) / count;
    }
}
