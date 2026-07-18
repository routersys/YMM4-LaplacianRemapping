namespace LaplacianRemapping;

internal static class LaplacianRemappingSettings
{
    public const float MinimumDetailAlpha = 0.25f;
    public const float MaximumDetailAlpha = 4f;
    public const float MaximumToneBeta = 2.5f;
    public const float MinimumSigma = 0.01f;
    public const float MaximumSigma = 1f;
    public const float NoiseFloorLow = 0.01f;
    public const float NoiseFloorHigh = 0.02f;
    public const float LuminanceWeightR = 0.2126f;
    public const float LuminanceWeightG = 0.7152f;
    public const float LuminanceWeightB = 0.0722f;
    public const float KernelWeightCenter = 0.4f;
    public const float KernelWeightNear = 0.25f;
    public const float KernelWeightFar = 0.05f;
    public const int MinimumTopSize = 2;
    public const int MaximumPyramidDepth = 16;
    public const int MaximumCanvasSize = 8192;
    public const int MaximumPixelCount = 16777216;
    public const int ScratchLength = 8;
    public const int ScratchHashSum = 0;
    public const int ScratchHashMix = 1;
    public const int ScratchMinX = 2;
    public const int ScratchMinY = 3;
    public const int ScratchMaxX = 4;
    public const int ScratchMaxY = 5;

    public static QualitySettings GetQuality(LaplacianRemappingQuality quality)
        => quality switch
        {
            LaplacianRemappingQuality.Balanced => new QualitySettings(7),
            LaplacianRemappingQuality.Ultra => new QualitySettings(15),
            _ => new QualitySettings(11),
        };

    public static int GetPyramidDepth(int width, int height)
    {
        var side = Math.Min(Math.Max(width, 1), Math.Max(height, 1));
        var depth = 1;
        while (side > MinimumTopSize && depth < MaximumPyramidDepth)
        {
            side = (side + 1) / 2;
            depth++;
        }
        return depth;
    }

    public static float GetDetailAlpha(float detail)
    {
        var clamped = Math.Clamp(detail, -1f, 1f);
        return clamped >= 0f
            ? 1f - clamped * (1f - MinimumDetailAlpha)
            : 1f - clamped * (MaximumDetailAlpha - 1f);
    }

    public static float GetToneBeta(float tone)
    {
        var clamped = Math.Clamp(tone, -1f, 1f);
        return clamped >= 0f
            ? 1f + clamped * (MaximumToneBeta - 1f)
            : 1f + clamped;
    }

    public static float GetSigma(float threshold)
        => Math.Clamp(threshold, MinimumSigma, MaximumSigma);

    internal readonly record struct QualitySettings(int SampleCount);
}
