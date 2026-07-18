using ComputeSharp;

namespace LaplacianRemapping;

[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct FillIntShader(
    ReadWriteBuffer<int> values,
    int length,
    int value) : IComputeShader
{
    private readonly ReadWriteBuffer<int> values = values;
    private readonly int length = length;
    private readonly int value = value;

    public void Execute()
    {
        var index = ThreadIds.X;
        if (index >= length)
            return;
        values[index] = value;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct InitScratchShader(
    ReadWriteBuffer<int> scratch) : IComputeShader
{
    private readonly ReadWriteBuffer<int> scratch = scratch;

    public void Execute()
    {
        if (ThreadIds.X != 0)
            return;
        scratch[0] = 0;
        scratch[1] = 0;
        scratch[2] = 2147483647;
        scratch[3] = 2147483647;
        scratch[4] = -1;
        scratch[5] = -1;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct HashBoundsShader(
    ReadWriteTexture2D<Bgra32, Float4> source,
    ReadWriteBuffer<int> scratch,
    int width,
    int height) : IComputeShader
{
    private readonly ReadWriteTexture2D<Bgra32, Float4> source = source;
    private readonly ReadWriteBuffer<int> scratch = scratch;
    private readonly int width = width;
    private readonly int height = height;

    public void Execute()
    {
        var x = ThreadIds.X;
        var y = ThreadIds.Y;
        if (x >= width || y >= height)
            return;

        var pixel = source[new Int2(x, y)];
        var packed = (uint)(pixel.W * 255f + 0.5f) << 24
            | (uint)(pixel.X * 255f + 0.5f) << 16
            | (uint)(pixel.Y * 255f + 0.5f) << 8
            | (uint)(pixel.Z * 255f + 0.5f);
        var mixed = packed * 0x9E3779B9u ^ (uint)(y * width + x) * 0x85EBCA6Bu;
        mixed ^= mixed >> 16;
        mixed *= 0xC2B2AE35u;
        mixed ^= mixed >> 13;
        Hlsl.InterlockedAdd(ref scratch[0], (int)mixed);
        Hlsl.InterlockedXor(ref scratch[1], (int)(mixed * 0x9E3779B9u));

        if (pixel.W > 0f)
        {
            Hlsl.InterlockedMin(ref scratch[2], x);
            Hlsl.InterlockedMin(ref scratch[3], y);
            Hlsl.InterlockedMax(ref scratch[4], x);
            Hlsl.InterlockedMax(ref scratch[5], y);
        }
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct LuminanceShader(
    ReadWriteTexture2D<Bgra32, Float4> source,
    ReadWriteBuffer<float> gaussian,
    int width,
    int height) : IComputeShader
{
    private readonly ReadWriteTexture2D<Bgra32, Float4> source = source;
    private readonly ReadWriteBuffer<float> gaussian = gaussian;
    private readonly int width = width;
    private readonly int height = height;

    public void Execute()
    {
        var x = ThreadIds.X;
        var y = ThreadIds.Y;
        if (x >= width || y >= height)
            return;

        var pixel = source[new Int2(x, y)];
        var alpha = pixel.W;
        var luminance = 0f;
        if (alpha > 0f)
        {
            var r = Hlsl.Saturate(pixel.X / alpha);
            var g = Hlsl.Saturate(pixel.Y / alpha);
            var b = Hlsl.Saturate(pixel.Z / alpha);
            luminance = r * LaplacianRemappingSettings.LuminanceWeightR
                + g * LaplacianRemappingSettings.LuminanceWeightG
                + b * LaplacianRemappingSettings.LuminanceWeightB;
        }
        gaussian[y * width + x] = luminance;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct DownsampleShader(
    ReadWriteBuffer<float> pyramid,
    int sourceOffset,
    int sourceWidth,
    int sourceHeight,
    int destinationOffset,
    int destinationWidth,
    int destinationHeight) : IComputeShader
{
    private readonly ReadWriteBuffer<float> pyramid = pyramid;
    private readonly int sourceOffset = sourceOffset;
    private readonly int sourceWidth = sourceWidth;
    private readonly int sourceHeight = sourceHeight;
    private readonly int destinationOffset = destinationOffset;
    private readonly int destinationWidth = destinationWidth;
    private readonly int destinationHeight = destinationHeight;

    public void Execute()
    {
        var x = ThreadIds.X;
        var y = ThreadIds.Y;
        if (x >= destinationWidth || y >= destinationHeight)
            return;

        var sum = 0f;
        for (var dy = -2; dy <= 2; dy++)
        {
            var weightY = LaplacianRemappingShaderMath.KernelWeight(dy);
            var sy = Hlsl.Clamp(2 * y + dy, 0, sourceHeight - 1);
            for (var dx = -2; dx <= 2; dx++)
            {
                var weightX = LaplacianRemappingShaderMath.KernelWeight(dx);
                var sx = Hlsl.Clamp(2 * x + dx, 0, sourceWidth - 1);
                sum += weightY * weightX * pyramid[sourceOffset + sy * sourceWidth + sx];
            }
        }
        pyramid[destinationOffset + y * destinationWidth + x] = sum;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct FillLevelShader(
    ReadWriteBuffer<float> pyramid,
    int offset,
    int width,
    int height) : IComputeShader
{
    private readonly ReadWriteBuffer<float> pyramid = pyramid;
    private readonly int offset = offset;
    private readonly int width = width;
    private readonly int height = height;

    public void Execute()
    {
        var x = ThreadIds.X;
        var y = ThreadIds.Y;
        if (x >= width || y >= height)
            return;
        pyramid[offset + y * width + x] = 0f;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct CopyLevelShader(
    ReadWriteBuffer<float> source,
    ReadWriteBuffer<float> destination,
    int sourceOffset,
    int destinationOffset,
    int width,
    int height) : IComputeShader
{
    private readonly ReadWriteBuffer<float> source = source;
    private readonly ReadWriteBuffer<float> destination = destination;
    private readonly int sourceOffset = sourceOffset;
    private readonly int destinationOffset = destinationOffset;
    private readonly int width = width;
    private readonly int height = height;

    public void Execute()
    {
        var x = ThreadIds.X;
        var y = ThreadIds.Y;
        if (x >= width || y >= height)
            return;
        destination[destinationOffset + y * width + x] = source[sourceOffset + y * width + x];
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct RemapShader(
    ReadWriteBuffer<float> gaussian,
    ReadWriteBuffer<float> work,
    int width,
    int height,
    float gamma,
    float sigma,
    float alphaExponent,
    float beta) : IComputeShader
{
    private readonly ReadWriteBuffer<float> gaussian = gaussian;
    private readonly ReadWriteBuffer<float> work = work;
    private readonly int width = width;
    private readonly int height = height;
    private readonly float gamma = gamma;
    private readonly float sigma = sigma;
    private readonly float alphaExponent = alphaExponent;
    private readonly float beta = beta;

    public void Execute()
    {
        var x = ThreadIds.X;
        var y = ThreadIds.Y;
        if (x >= width || y >= height)
            return;

        var index = y * width + x;
        var intensity = gaussian[index];
        var difference = intensity - gamma;
        var amplitude = Hlsl.Abs(difference);
        var sign = difference < 0f ? -1f : 1f;
        float remapped;
        if (amplitude <= sigma)
        {
            var delta = amplitude / sigma;
            var detail = Hlsl.Pow(delta, alphaExponent);
            if (alphaExponent < 1f)
            {
                var tau = Hlsl.SmoothStep(
                    LaplacianRemappingSettings.NoiseFloorLow,
                    LaplacianRemappingSettings.NoiseFloorHigh,
                    amplitude);
                detail = tau * detail + (1f - tau) * delta;
            }
            remapped = gamma + sign * sigma * detail;
        }
        else
        {
            remapped = gamma + sign * (beta * (amplitude - sigma) + sigma);
        }
        work[index] = remapped;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct AccumulateShader(
    ReadWriteBuffer<float> accumulation,
    ReadWriteBuffer<float> work,
    ReadWriteBuffer<float> gaussian,
    int levelOffset,
    int levelWidth,
    int levelHeight,
    int coarseOffset,
    int coarseWidth,
    int coarseHeight,
    float gamma,
    float inverseSpacing) : IComputeShader
{
    private readonly ReadWriteBuffer<float> accumulation = accumulation;
    private readonly ReadWriteBuffer<float> work = work;
    private readonly ReadWriteBuffer<float> gaussian = gaussian;
    private readonly int levelOffset = levelOffset;
    private readonly int levelWidth = levelWidth;
    private readonly int levelHeight = levelHeight;
    private readonly int coarseOffset = coarseOffset;
    private readonly int coarseWidth = coarseWidth;
    private readonly int coarseHeight = coarseHeight;
    private readonly float gamma = gamma;
    private readonly float inverseSpacing = inverseSpacing;

    public void Execute()
    {
        var x = ThreadIds.X;
        var y = ThreadIds.Y;
        if (x >= levelWidth || y >= levelHeight)
            return;

        var index = levelOffset + y * levelWidth + x;
        var reference = Hlsl.Saturate(gaussian[index]);
        var weight = 1f - Hlsl.Abs(reference - gamma) * inverseSpacing;
        if (weight <= 0f)
            return;

        var baseX = x >> 1;
        var baseY = y >> 1;
        var upsampled = 0f;
        for (var ky = -1; ky <= 1; ky++)
        {
            var weightY = LaplacianRemappingShaderMath.UpsampleWeight(2 * (baseY + ky) - y);
            if (weightY <= 0f)
                continue;
            var cy = Hlsl.Clamp(baseY + ky, 0, coarseHeight - 1);
            for (var kx = -1; kx <= 1; kx++)
            {
                var weightX = LaplacianRemappingShaderMath.UpsampleWeight(2 * (baseX + kx) - x);
                if (weightX <= 0f)
                    continue;
                var cx = Hlsl.Clamp(baseX + kx, 0, coarseWidth - 1);
                upsampled += weightY * weightX * work[coarseOffset + cy * coarseWidth + cx];
            }
        }

        var laplacian = work[index] - upsampled;
        accumulation[index] += weight * laplacian;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct CollapseShader(
    ReadWriteBuffer<float> work,
    ReadWriteBuffer<float> accumulation,
    int levelOffset,
    int levelWidth,
    int levelHeight,
    int coarseOffset,
    int coarseWidth,
    int coarseHeight) : IComputeShader
{
    private readonly ReadWriteBuffer<float> work = work;
    private readonly ReadWriteBuffer<float> accumulation = accumulation;
    private readonly int levelOffset = levelOffset;
    private readonly int levelWidth = levelWidth;
    private readonly int levelHeight = levelHeight;
    private readonly int coarseOffset = coarseOffset;
    private readonly int coarseWidth = coarseWidth;
    private readonly int coarseHeight = coarseHeight;

    public void Execute()
    {
        var x = ThreadIds.X;
        var y = ThreadIds.Y;
        if (x >= levelWidth || y >= levelHeight)
            return;

        var baseX = x >> 1;
        var baseY = y >> 1;
        var upsampled = 0f;
        for (var ky = -1; ky <= 1; ky++)
        {
            var weightY = LaplacianRemappingShaderMath.UpsampleWeight(2 * (baseY + ky) - y);
            if (weightY <= 0f)
                continue;
            var cy = Hlsl.Clamp(baseY + ky, 0, coarseHeight - 1);
            for (var kx = -1; kx <= 1; kx++)
            {
                var weightX = LaplacianRemappingShaderMath.UpsampleWeight(2 * (baseX + kx) - x);
                if (weightX <= 0f)
                    continue;
                var cx = Hlsl.Clamp(baseX + kx, 0, coarseWidth - 1);
                upsampled += weightY * weightX * work[coarseOffset + cy * coarseWidth + cx];
            }
        }

        var index = levelOffset + y * levelWidth + x;
        work[index] = accumulation[index] + upsampled;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct RenderShader(
    ReadWriteBuffer<float> work,
    ReadWriteTexture2D<Bgra32, Float4> source,
    ReadWriteTexture2D<Bgra32, Float4> output,
    int rectOffsetX,
    int rectOffsetY,
    int rectWidth,
    int rectHeight,
    int width,
    int height) : IComputeShader
{
    private readonly ReadWriteBuffer<float> work = work;
    private readonly ReadWriteTexture2D<Bgra32, Float4> source = source;
    private readonly ReadWriteTexture2D<Bgra32, Float4> output = output;
    private readonly int rectOffsetX = rectOffsetX;
    private readonly int rectOffsetY = rectOffsetY;
    private readonly int rectWidth = rectWidth;
    private readonly int rectHeight = rectHeight;
    private readonly int width = width;
    private readonly int height = height;

    public void Execute()
    {
        if (ThreadIds.X >= rectWidth || ThreadIds.Y >= rectHeight)
            return;
        var px = ThreadIds.X + rectOffsetX;
        var py = ThreadIds.Y + rectOffsetY;
        var pixel = source[new Int2(px, py)];
        var alpha = pixel.W;
        if (alpha <= 0f)
        {
            output[ThreadIds.XY] = new Float4(0f, 0f, 0f, 0f);
            return;
        }

        var r = Hlsl.Saturate(pixel.X / alpha);
        var g = Hlsl.Saturate(pixel.Y / alpha);
        var b = Hlsl.Saturate(pixel.Z / alpha);
        var luminance = r * LaplacianRemappingSettings.LuminanceWeightR
            + g * LaplacianRemappingSettings.LuminanceWeightG
            + b * LaplacianRemappingSettings.LuminanceWeightB;
        var filtered = Hlsl.Max(work[py * width + px], 0f);

        Float3 color;
        if (luminance <= 1e-6f)
        {
            var lifted = Hlsl.Saturate(filtered);
            color = new Float3(lifted, lifted, lifted);
        }
        else
        {
            var ratio = filtered / luminance;
            color = Hlsl.Saturate(new Float3(r * ratio, g * ratio, b * ratio));
        }
        output[ThreadIds.XY] = new Float4(color.X * alpha, color.Y * alpha, color.Z * alpha, alpha);
    }
}

internal static class LaplacianRemappingShaderMath
{
    public static float KernelWeight(int offset)
    {
        if (offset == 0)
            return LaplacianRemappingSettings.KernelWeightCenter;
        return offset == -1 || offset == 1
            ? LaplacianRemappingSettings.KernelWeightNear
            : LaplacianRemappingSettings.KernelWeightFar;
    }

    public static float UpsampleWeight(int offset)
    {
        if (offset == 0)
            return 2f * LaplacianRemappingSettings.KernelWeightCenter;
        if (offset == -1 || offset == 1)
            return 2f * LaplacianRemappingSettings.KernelWeightNear;
        return offset == -2 || offset == 2 ? 2f * LaplacianRemappingSettings.KernelWeightFar : 0f;
    }
}
