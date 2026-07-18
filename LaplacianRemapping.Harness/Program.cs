using System.Diagnostics;
using ComputeSharp;
using LaplacianRemapping;

var width = 1280;
var height = 720;
var outputDirectory = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : Path.Combine(AppContext.BaseDirectory, "harness-output");
Directory.CreateDirectory(outputDirectory);

using var pipeline = LaplacianRemappingPipeline.TryCreate();
if (pipeline is null)
{
    Console.WriteLine("Direct3D 12 is unavailable.");
    return 1;
}

var source = CreateTestImage(width, height);
var destination = new int[source.Length];

if (args.Contains("--golden"))
{
    var goldenCases = new (string Name, LaplacianRemappingPipeline.Parameters Parameters)[]
    {
        ("balanced-default", new(LaplacianRemappingQuality.Balanced, 0.5f, 0f, 0.3f)),
        ("high-default", new(LaplacianRemappingQuality.High, 0.5f, 0f, 0.3f)),
        ("ultra-default", new(LaplacianRemappingQuality.Ultra, 0.5f, 0f, 0.3f)),
        ("detail-max", new(LaplacianRemappingQuality.High, 1f, 0f, 0.3f)),
        ("detail-min", new(LaplacianRemappingQuality.High, -1f, 0f, 0.3f)),
        ("tone-min", new(LaplacianRemappingQuality.High, 0f, -1f, 0.3f)),
        ("tone-max", new(LaplacianRemappingQuality.High, 0f, 1f, 0.3f)),
        ("sigma-small", new(LaplacianRemappingQuality.High, 0.5f, -0.5f, 0.05f)),
    };
    foreach (var (name, goldenParameters) in goldenCases)
    {
        var parameters = goldenParameters;
        pipeline.Process(source, destination, width, height, in parameters);
        var bytes = new byte[destination.Length * sizeof(int)];
        Buffer.BlockCopy(destination, 0, bytes, 0, bytes.Length);
        Console.WriteLine($"{name}: {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))}");
    }
    return 0;
}

foreach (var quality in new[] { LaplacianRemappingQuality.Balanced, LaplacianRemappingQuality.High, LaplacianRemappingQuality.Ultra })
{
    var parameters = new LaplacianRemappingPipeline.Parameters(quality, 0.5f, -0.3f, 0.3f);
    pipeline.Process(source, destination, width, height, in parameters);
    pipeline.Process(source, destination, width, height, in parameters);
    var stopwatch = Stopwatch.StartNew();
    const int frames = 5;
    for (var frame = 0; frame < frames; frame++)
        pipeline.Process(source, destination, width, height, in parameters);
    stopwatch.Stop();
    Console.WriteLine($"{quality}: {stopwatch.Elapsed.TotalMilliseconds / frames:F2} ms/frame ({width}x{height})");
}

{
    var device = GraphicsDevice.GetDefault();
    using var sourceTexture = device.AllocateReadWriteTexture2D<Bgra32, Float4>(width, height);
    var pixels = new Bgra32[source.Length];
    for (var index = 0; index < source.Length; index++)
        pixels[index].PackedValue = unchecked((uint)source[index]);
    sourceTexture.CopyFrom(pixels);
    var parameters = new LaplacianRemappingPipeline.Parameters(LaplacianRemappingQuality.High, 0.5f, -0.3f, 0.3f);

    pipeline.Simulate(sourceTexture, width, height, in parameters);
    var stopwatch = Stopwatch.StartNew();
    pipeline.Simulate(sourceTexture, width, height, parameters with { Detail = 0.6f });
    stopwatch.Stop();
    Console.WriteLine($"structure recompute: {stopwatch.Elapsed.TotalMilliseconds:F2} ms");

    if (pipeline.TryGetVisibleBounds(width, height, out var rect))
    {
        using var rectOutput = device.AllocateReadWriteTexture2D<Bgra32, Float4>(rect.Width, rect.Height);
        pipeline.RenderVisible(sourceTexture, rectOutput, width, height, rect);
        pipeline.WaitForCompletion();
        stopwatch.Restart();
        const int rectFrames = 20;
        for (var frame = 0; frame < rectFrames; frame++)
        {
            pipeline.Simulate(sourceTexture, width, height, in parameters);
            pipeline.TryGetVisibleBounds(width, height, out rect);
            pipeline.RenderVisible(sourceTexture, rectOutput, width, height, rect);
        }
        pipeline.WaitForCompletion();
        stopwatch.Stop();
        Console.WriteLine($"cached frame with rect {rect.Width}x{rect.Height} at ({rect.X},{rect.Y}): {stopwatch.Elapsed.TotalMilliseconds / rectFrames:F2} ms/frame");
    }
}

foreach (var detail in new[] { -1f, -0.5f, 0f, 0.5f, 1f })
{
    var parameters = new LaplacianRemappingPipeline.Parameters(LaplacianRemappingQuality.High, detail, 0f, 0.3f);
    pipeline.Process(source, destination, width, height, in parameters);
    Console.WriteLine($"detail={detail:F2}");
    WriteBmp(Path.Combine(outputDirectory, $"detail{(int)(detail * 100):+000;-000}.bmp"), destination, width, height);
}

foreach (var tone in new[] { -1f, -0.5f, 0.5f, 1f })
{
    var parameters = new LaplacianRemappingPipeline.Parameters(LaplacianRemappingQuality.High, 0f, tone, 0.3f);
    pipeline.Process(source, destination, width, height, in parameters);
    Console.WriteLine($"tone={tone:F2}");
    WriteBmp(Path.Combine(outputDirectory, $"tone{(int)(tone * 100):+000;-000}.bmp"), destination, width, height);
}

foreach (var threshold in new[] { 0.1f, 0.6f })
{
    var parameters = new LaplacianRemappingPipeline.Parameters(LaplacianRemappingQuality.High, 1f, 0f, threshold);
    pipeline.Process(source, destination, width, height, in parameters);
    Console.WriteLine($"threshold={threshold:F2}");
    WriteBmp(Path.Combine(outputDirectory, $"threshold{(int)(threshold * 100):D3}.bmp"), destination, width, height);
}

WriteBmp(Path.Combine(outputDirectory, "source.bmp"), source, width, height);
Console.WriteLine($"images written to {outputDirectory}");
return 0;

static int[] CreateTestImage(int width, int height)
{
    var pixels = new int[width * height];
    for (var y = 40; y < height - 40; y++)
    {
        for (var x = 40; x < width - 40; x++)
        {
            var gradient = 40 + 180 * (x - 40) / (width - 80);
            var texture = (int)(24 * Math.Sin(x * 0.55) * Math.Sin(y * 0.55));
            var vignette = y < height / 2 ? 0 : -30;
            var value = Math.Clamp(gradient + texture + vignette, 0, 255);
            var r = value;
            var g = Math.Clamp(value + 10, 0, 255);
            var b = Math.Clamp(value + 25, 0, 255);
            pixels[y * width + x] = unchecked((int)0xFF000000) | r << 16 | g << 8 | b;
        }
    }
    for (var y = height / 2 - 60; y < height / 2 + 60; y++)
    {
        for (var x = width / 2 - 60; x < width / 2 + 60; x++)
        {
            var value = ((x / 6 + y / 6) & 1) == 0 ? 230 : 25;
            pixels[y * width + x] = unchecked((int)0xFF000000) | value << 16 | value << 8 | value;
        }
    }
    return pixels;
}

static void WriteBmp(string path, int[] pixels, int width, int height)
{
    var stride = width * 3;
    var padding = (4 - stride % 4) % 4;
    var dataSize = (stride + padding) * height;
    using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
    using var writer = new BinaryWriter(stream);
    writer.Write((byte)'B');
    writer.Write((byte)'M');
    writer.Write(54 + dataSize);
    writer.Write(0);
    writer.Write(54);
    writer.Write(40);
    writer.Write(width);
    writer.Write(height);
    writer.Write((short)1);
    writer.Write((short)24);
    writer.Write(0);
    writer.Write(dataSize);
    writer.Write(2835);
    writer.Write(2835);
    writer.Write(0);
    writer.Write(0);
    var pad = new byte[padding];
    for (var y = height - 1; y >= 0; y--)
    {
        for (var x = 0; x < width; x++)
        {
            var pixel = pixels[y * width + x];
            writer.Write((byte)(pixel & 255));
            writer.Write((byte)((pixel >> 8) & 255));
            writer.Write((byte)((pixel >> 16) & 255));
        }
        writer.Write(pad);
    }
}
