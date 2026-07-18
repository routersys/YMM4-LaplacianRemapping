using System.Runtime.InteropServices;
using ComputeSharp;

namespace LaplacianRemapping;

internal sealed class LaplacianRemappingPipeline : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly ReadWriteBuffer<int> _scratch;
    private readonly ReadBackBuffer<int> _scratchReadBack;
    private readonly int[] _levelOffsets;
    private readonly int[] _levelWidths;
    private readonly int[] _levelHeights;
    private ReadWriteBuffer<float>? _gaussian;
    private ReadWriteBuffer<float>? _work;
    private ReadWriteBuffer<float>? _accumulation;
    private StructureKey? _structureKey;
    private int _width;
    private int _height;
    private int _depth;
    private int _cachedMinX;
    private int _cachedMinY;
    private int _cachedMaxX;
    private int _cachedMaxY;
    private ReadWriteTexture2D<Bgra32, Float4>? _packedSource;
    private ReadWriteTexture2D<Bgra32, Float4>? _packedOutput;
    private int _packedWidth;
    private int _packedHeight;

    private LaplacianRemappingPipeline(GraphicsDevice device)
    {
        _device = device;
        _scratch = device.AllocateReadWriteBuffer<int>(LaplacianRemappingSettings.ScratchLength);
        _scratchReadBack = device.AllocateReadBackBuffer<int>(LaplacianRemappingSettings.ScratchLength);
        _levelOffsets = new int[LaplacianRemappingSettings.MaximumPyramidDepth];
        _levelWidths = new int[LaplacianRemappingSettings.MaximumPyramidDepth];
        _levelHeights = new int[LaplacianRemappingSettings.MaximumPyramidDepth];
    }

    public static LaplacianRemappingPipeline? TryCreate()
    {
        try
        {
            return new LaplacianRemappingPipeline(GraphicsDevice.GetDefault());
        }
        catch
        {
            return null;
        }
    }

    public static LaplacianRemappingPipeline? TryCreate(GraphicsDevice device)
    {
        try
        {
            return new LaplacianRemappingPipeline(device);
        }
        catch
        {
            return null;
        }
    }

    internal void WaitForCompletion()
    {
        _device.For(1, new FillIntShader(_scratch, 0, 0));
    }

    public void Process(ReadOnlySpan<int> source, Span<int> destination, int width, int height, in Parameters parameters)
    {
        var pixelCount = checked(width * height);
        EnsureGrid(width, height);
        EnsurePackedTextures(width, height);
        var sourceTexture = _packedSource!;
        var outputTexture = _packedOutput!;
        sourceTexture.CopyFrom(MemoryMarshal.Cast<int, Bgra32>(source[..pixelCount]));
        using (ComputeContext context = _device.CreateComputeContext())
            RecordFullPipeline(in context, sourceTexture, outputTexture, width, height, in parameters);
        outputTexture.CopyTo(MemoryMarshal.Cast<int, Bgra32>(destination[..pixelCount]));
    }

    public void Process(
        ReadWriteTexture2D<Bgra32, Float4> source,
        ReadWriteTexture2D<Bgra32, Float4> destination,
        int width,
        int height,
        in Parameters parameters)
    {
        EnsureGrid(width, height);
        using ComputeContext context = _device.CreateComputeContext();
        RecordFullPipeline(in context, source, destination, width, height, in parameters);
        context.Submit();
    }

    internal void ProcessSharedAndWait(
        ReadWriteTexture2D<Bgra32, Float4> source,
        ReadWriteTexture2D<Bgra32, Float4> destination,
        int width,
        int height,
        in Parameters parameters)
    {
        EnsureGrid(width, height);
        using ComputeContext context = _device.CreateComputeContext();
        RecordFullPipeline(in context, source, destination, width, height, in parameters);
    }

    internal bool Simulate(
        ReadWriteTexture2D<Bgra32, Float4> source,
        int width,
        int height,
        in Parameters parameters)
    {
        EnsureGrid(width, height);
        using (ComputeContext context = _device.CreateComputeContext())
            RecordHashStage(in context, source, width, height);
        _scratchReadBack.CopyFrom(_scratch);
        var hashed = _scratchReadBack.Span;
        _cachedMinX = hashed[LaplacianRemappingSettings.ScratchMinX];
        _cachedMinY = hashed[LaplacianRemappingSettings.ScratchMinY];
        _cachedMaxX = hashed[LaplacianRemappingSettings.ScratchMaxX];
        _cachedMaxY = hashed[LaplacianRemappingSettings.ScratchMaxY];
        var key = new StructureKey(
            hashed[LaplacianRemappingSettings.ScratchHashSum],
            hashed[LaplacianRemappingSettings.ScratchHashMix],
            width,
            height,
            parameters.Quality,
            parameters.Detail,
            parameters.Tone,
            parameters.Threshold);
        if (_structureKey == key)
            return false;

        var derived = Derive(in parameters);
        var minBits = hashed[LaplacianRemappingSettings.ScratchLuminanceMinBits];
        var maxBits = hashed[LaplacianRemappingSettings.ScratchLuminanceMaxBits];
        var luminanceMin = minBits >= 0 && minBits <= maxBits ? BitConverter.Int32BitsToSingle(minBits) : 0f;
        var luminanceMax = minBits >= 0 && minBits <= maxBits ? BitConverter.Int32BitsToSingle(maxBits) : 1f;
        using (ComputeContext context = _device.CreateComputeContext())
        {
            RecordLuminanceStage(in context, source, width, height);
            RecordFilterStage(in context, in derived, luminanceMin, luminanceMax);
        }
        _structureKey = key;
        return true;
    }

    internal bool TryGetVisibleBounds(int width, int height, out PixelRect rect)
    {
        rect = default;
        if (_cachedMaxX < _cachedMinX || _cachedMaxY < _cachedMinY)
            return false;

        var left = Math.Clamp(_cachedMinX & ~3, 0, width);
        var top = Math.Clamp(_cachedMinY & ~3, 0, height);
        var right = Math.Clamp(_cachedMaxX + 1, 0, width);
        var bottom = Math.Clamp(_cachedMaxY + 1, 0, height);
        var rectWidth = Math.Min((right - left + 3) & ~3, width - left);
        var rectHeight = Math.Min((bottom - top + 3) & ~3, height - top);
        if (rectWidth <= 0 || rectHeight <= 0)
            return false;

        rect = new PixelRect(left, top, rectWidth, rectHeight);
        return true;
    }

    internal void RenderVisible(
        ReadWriteTexture2D<Bgra32, Float4> source,
        ReadWriteTexture2D<Bgra32, Float4> output,
        int width,
        int height,
        PixelRect rect)
    {
        using ComputeContext context = _device.CreateComputeContext();
        RecordRenderStage(in context, source, output, rect, width, height);
    }

    private void RecordFullPipeline(
        in ComputeContext context,
        ReadWriteTexture2D<Bgra32, Float4> source,
        ReadWriteTexture2D<Bgra32, Float4> output,
        int width,
        int height,
        in Parameters parameters)
    {
        _structureKey = null;
        var derived = Derive(in parameters);
        RecordLuminanceStage(in context, source, width, height);
        RecordFilterStage(in context, in derived, 0f, 1f);
        RecordRenderStage(in context, source, output, new PixelRect(0, 0, width, height), width, height);
    }

    private void RecordHashStage(in ComputeContext context, ReadWriteTexture2D<Bgra32, Float4> source, int width, int height)
    {
        context.For(1, new InitScratchShader(_scratch));
        context.Barrier(_scratch);
        context.For(width, height, new HashBoundsShader(source, _scratch, width, height));
        context.Barrier(_scratch);
    }

    private void RecordLuminanceStage(in ComputeContext context, ReadWriteTexture2D<Bgra32, Float4> source, int width, int height)
    {
        context.For(width, height, new LuminanceShader(source, _gaussian!, width, height));
        context.Barrier(_gaussian!);
    }

    private void RecordFilterStage(in ComputeContext context, in DerivedValues derived, float luminanceMin, float luminanceMax)
    {
        var gaussian = _gaussian!;
        var work = _work!;
        var accumulation = _accumulation!;
        var depth = _depth;

        for (var level = 1; level < depth; level++)
        {
            context.For(_levelWidths[level], _levelHeights[level], new DownsampleShader(
                gaussian,
                _levelOffsets[level - 1], _levelWidths[level - 1], _levelHeights[level - 1],
                _levelOffsets[level], _levelWidths[level], _levelHeights[level]));
            context.Barrier(gaussian);
        }

        for (var level = 0; level < depth - 1; level++)
            context.For(_levelWidths[level], _levelHeights[level], new FillLevelShader(
                accumulation, _levelOffsets[level], _levelWidths[level], _levelHeights[level]));
        context.Barrier(accumulation);

        var spacing = 1f / (derived.SampleCount - 1);
        var inverseSpacing = derived.SampleCount - 1f;
        for (var sample = 0; sample < derived.SampleCount; sample++)
        {
            var gamma = sample * spacing;
            if (gamma < luminanceMin - 2f * spacing || gamma > luminanceMax + 2f * spacing)
                continue;
            context.For(_levelWidths[0], _levelHeights[0], new RemapShader(
                gaussian, work, _levelWidths[0], _levelHeights[0],
                gamma, derived.Sigma, derived.AlphaExponent, derived.Beta));
            context.Barrier(work);
            for (var level = 1; level < depth; level++)
            {
                context.For(_levelWidths[level], _levelHeights[level], new DownsampleShader(
                    work,
                    _levelOffsets[level - 1], _levelWidths[level - 1], _levelHeights[level - 1],
                    _levelOffsets[level], _levelWidths[level], _levelHeights[level]));
                context.Barrier(work);
            }
            for (var level = 0; level < depth - 1; level++)
                context.For(_levelWidths[level], _levelHeights[level], new AccumulateShader(
                    accumulation, work, gaussian,
                    _levelOffsets[level], _levelWidths[level], _levelHeights[level],
                    _levelOffsets[level + 1], _levelWidths[level + 1], _levelHeights[level + 1],
                    gamma, inverseSpacing));
            context.Barrier(accumulation);
            context.Barrier(work);
        }

        var residual = depth - 1;
        context.For(_levelWidths[residual], _levelHeights[residual], new CopyLevelShader(
            gaussian, work, _levelOffsets[residual], _levelOffsets[residual], _levelWidths[residual], _levelHeights[residual]));
        context.Barrier(work);
        for (var level = depth - 2; level >= 0; level--)
        {
            context.For(_levelWidths[level], _levelHeights[level], new CollapseShader(
                work, accumulation,
                _levelOffsets[level], _levelWidths[level], _levelHeights[level],
                _levelOffsets[level + 1], _levelWidths[level + 1], _levelHeights[level + 1]));
            context.Barrier(work);
        }
    }

    private void RecordRenderStage(
        in ComputeContext context,
        ReadWriteTexture2D<Bgra32, Float4> source,
        ReadWriteTexture2D<Bgra32, Float4> output,
        PixelRect rect,
        int width,
        int height)
    {
        context.For(rect.Width, rect.Height, new RenderShader(
            _work!, source, output,
            rect.X, rect.Y, rect.Width, rect.Height, width, height));
    }

    private static DerivedValues Derive(in Parameters parameters)
    {
        var settings = LaplacianRemappingSettings.GetQuality(parameters.Quality);
        return new DerivedValues(
            LaplacianRemappingSettings.GetDetailAlpha(parameters.Detail),
            LaplacianRemappingSettings.GetToneBeta(parameters.Tone),
            LaplacianRemappingSettings.GetSigma(parameters.Threshold),
            settings.SampleCount);
    }

    private void EnsureGrid(int width, int height)
    {
        if (_width == width && _height == height)
            return;

        DisposeGridBuffers();
        var depth = LaplacianRemappingSettings.GetPyramidDepth(width, height);
        var levelWidth = width;
        var levelHeight = height;
        var total = 0;
        for (var level = 0; level < depth; level++)
        {
            _levelOffsets[level] = total;
            _levelWidths[level] = levelWidth;
            _levelHeights[level] = levelHeight;
            total = checked(total + levelWidth * levelHeight);
            levelWidth = (levelWidth + 1) / 2;
            levelHeight = (levelHeight + 1) / 2;
        }
        _gaussian = _device.AllocateReadWriteBuffer<float>(total);
        _work = _device.AllocateReadWriteBuffer<float>(total);
        _accumulation = _device.AllocateReadWriteBuffer<float>(total);
        _width = width;
        _height = height;
        _depth = depth;
    }

    private void EnsurePackedTextures(int width, int height)
    {
        if (_packedWidth == width && _packedHeight == height)
            return;

        _packedSource?.Dispose();
        _packedOutput?.Dispose();
        _packedSource = _device.AllocateReadWriteTexture2D<Bgra32, Float4>(width, height);
        _packedOutput = _device.AllocateReadWriteTexture2D<Bgra32, Float4>(width, height);
        _packedWidth = width;
        _packedHeight = height;
    }

    private void DisposeGridBuffers()
    {
        _gaussian?.Dispose();
        _work?.Dispose();
        _accumulation?.Dispose();
        _gaussian = null;
        _work = null;
        _accumulation = null;
        _structureKey = null;
        _cachedMinX = 0;
        _cachedMinY = 0;
        _cachedMaxX = -1;
        _cachedMaxY = -1;
        _width = 0;
        _height = 0;
        _depth = 0;
    }

    public void Dispose()
    {
        DisposeGridBuffers();
        _packedSource?.Dispose();
        _packedOutput?.Dispose();
        _packedSource = null;
        _packedOutput = null;
        _packedWidth = 0;
        _packedHeight = 0;
        _scratchReadBack.Dispose();
        _scratch.Dispose();
    }

    internal readonly record struct PixelRect(int X, int Y, int Width, int Height);

    private readonly record struct StructureKey(
        int PixelHashSum,
        int PixelHashMix,
        int Width,
        int Height,
        LaplacianRemappingQuality Quality,
        float Detail,
        float Tone,
        float Threshold);

    private readonly record struct DerivedValues(
        float AlphaExponent,
        float Beta,
        float Sigma,
        int SampleCount);

    internal readonly record struct Parameters(
        LaplacianRemappingQuality Quality,
        float Detail,
        float Tone,
        float Threshold);
}
