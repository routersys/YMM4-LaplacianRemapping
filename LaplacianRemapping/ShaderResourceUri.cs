namespace LaplacianRemapping;

internal static class ShaderResourceUri
{
    public static Uri Get(string shaderName) => new($"pack://application:,,,/LaplacianRemapping;component/Shaders/{shaderName}.cso", UriKind.Absolute);
}
