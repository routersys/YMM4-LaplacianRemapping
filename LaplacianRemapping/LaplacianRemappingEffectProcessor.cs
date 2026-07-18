using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Player.Video.Effects;

namespace LaplacianRemapping;

internal sealed class LaplacianRemappingEffectProcessor : VideoEffectProcessorBase
{
    private readonly IGraphicsDevicesAndContext _devices;
    private readonly LaplacianRemappingEffect _item;
    private LaplacianRemappingGpuInterop? _interop;
    private LaplacianRemappingPipeline? _pipeline;
    private LaplacianRemappingCustomEffect? _effect;
    private Crop? _outputCrop;
    private ID2D1Image? _outputCropOutput;
    private AffineTransform2D? _outputTransform;
    private ID2D1Image? _outputTransformOutput;
    private bool _isFirst = true;
    private bool _hasOutput;
    private bool _hasOutputOffset;
    private bool _hasCropRect;
    private bool _hasRenderState;
    private Vector2 _outputOffset;
    private Vector4 _cropRect;
    private Parameters _parameters;
    private RenderState _renderState;

    public LaplacianRemappingEffectProcessor(IGraphicsDevicesAndContext devices, LaplacianRemappingEffect item)
        : base(devices)
    {
        _devices = devices;
        _item = item;
    }

    public override DrawDescription Update(EffectDescription effectDescription)
    {
        if (IsPassThroughEffect || _effect is null || _outputCrop is null || _outputTransform is null || _outputTransformOutput is null || _interop is null || _pipeline is null || input is null)
            return effectDescription.DrawDescription;

        var frame = effectDescription.ItemPosition.Frame;
        var length = effectDescription.ItemDuration.Frame;
        var fps = effectDescription.FPS;
        var parameters = new Parameters(
            (float)(_item.Amount.GetValue(frame, length, fps) / 100.0),
            (float)(_item.Detail.GetValue(frame, length, fps) / 100.0),
            (float)(_item.Tone.GetValue(frame, length, fps) / 100.0),
            (float)(_item.Threshold.GetValue(frame, length, fps) / 100.0),
            _item.Quality);

        if (_isFirst || _parameters.Amount != parameters.Amount)
            _effect.Amount = parameters.Amount;

        if (parameters.Amount <= 0f || (parameters.Detail == 0f && parameters.Tone == 0f))
        {
            _effect.Amount = 0f;
            _parameters = parameters;
            _isFirst = true;
            return effectDescription.DrawDescription;
        }

        var bounds = _devices.DeviceContext.GetImageLocalBounds(input);
        var widthValue = Math.Ceiling((double)bounds.Right - bounds.Left);
        var heightValue = Math.Ceiling((double)bounds.Bottom - bounds.Top);
        if (!double.IsFinite(widthValue) || !double.IsFinite(heightValue) ||
            !float.IsFinite(bounds.Left) || !float.IsFinite(bounds.Top) ||
            widthValue <= 0d || heightValue <= 0d)
        {
            _effect.Amount = 0f;
            _isFirst = true;
            return effectDescription.DrawDescription;
        }

        if (Math.Max(widthValue, heightValue) > LaplacianRemappingSettings.MaximumCanvasSize ||
            widthValue * heightValue > LaplacianRemappingSettings.MaximumPixelCount)
        {
            _effect.Amount = 0f;
            _isFirst = true;
            return effectDescription.DrawDescription;
        }
        var itemWidth = (int)widthValue;
        var itemHeight = (int)heightValue;

        _interop.EnsureSource(itemWidth, itemHeight);
        _interop.RenderInput(input, new Vortice.RawRectF(bounds.Left, bounds.Top, bounds.Left + itemWidth, bounds.Top + itemHeight));

        var pipelineParameters = new LaplacianRemappingPipeline.Parameters(
            parameters.Quality,
            Math.Clamp(parameters.Detail, -1f, 1f),
            Math.Clamp(parameters.Tone, -1f, 1f),
            Math.Clamp(parameters.Threshold, LaplacianRemappingSettings.MinimumSigma, LaplacianRemappingSettings.MaximumSigma));

        bool structureChanged;
        _interop.BeginCompute();
        try
        {
            structureChanged = _pipeline.Simulate(_interop.SourceTexture, itemWidth, itemHeight, in pipelineParameters);
        }
        finally
        {
            _interop.EndCompute();
        }

        if (!_pipeline.TryGetVisibleBounds(itemWidth, itemHeight, out var rect))
        {
            _effect.Amount = 0f;
            _parameters = parameters;
            _isFirst = true;
            _hasRenderState = false;
            return effectDescription.DrawDescription;
        }

        if (!_interop.OutputCovers(rect.Width, rect.Height))
            _outputCrop.SetInput(0, null, true);
        var outputChanged = _interop.EnsureOutput(rect.Width, rect.Height);
        var renderState = new RenderState(rect);
        if (structureChanged || outputChanged || !_hasOutput || !_hasRenderState || _renderState != renderState)
        {
            _interop.BeginCompute();
            try
            {
                _pipeline.RenderVisible(_interop.SourceTexture, _interop.OutputTexture, itemWidth, itemHeight, rect);
            }
            finally
            {
                _interop.EndCompute();
            }
            _renderState = renderState;
            _hasRenderState = true;
        }

        if (outputChanged || !_hasOutput)
        {
            _outputCrop.SetInput(0, _interop.OutputBitmap, true);
            _effect.SetInput(1, _outputTransformOutput, true);
        }
        var cropRect = new Vector4(0f, 0f, rect.Width, rect.Height);
        if (!_hasCropRect || _cropRect != cropRect)
        {
            _outputCrop.Rectangle = cropRect;
            _cropRect = cropRect;
            _hasCropRect = true;
        }
        var outputOffset = new Vector2(bounds.Left + rect.X, bounds.Top + rect.Y);
        if (!_hasOutputOffset || _outputOffset != outputOffset)
        {
            _outputTransform.TransformMatrix = Matrix3x2.CreateTranslation(outputOffset);
            _outputOffset = outputOffset;
            _hasOutputOffset = true;
        }
        _hasOutput = true;
        _parameters = parameters;
        _isFirst = false;
        return effectDescription.DrawDescription;
    }

    protected override ID2D1Image? CreateEffect(IGraphicsDevicesAndContext devices)
    {
        var interop = LaplacianRemappingGpuInterop.TryCreate(devices);
        if (interop is null)
            return null;
        var pipeline = LaplacianRemappingPipeline.TryCreate(interop.Device);
        if (pipeline is null)
        {
            interop.Dispose();
            return null;
        }

        LaplacianRemappingCustomEffect? effect = null;
        Crop? outputCrop = null;
        ID2D1Image? outputCropOutput = null;
        AffineTransform2D? outputTransform = null;
        ID2D1Image? outputTransformOutput = null;
        ID2D1Image? output = null;
        try
        {
            effect = new LaplacianRemappingCustomEffect(devices);
            if (!effect.IsEnabled)
            {
                effect.Dispose();
                pipeline.Dispose();
                interop.Dispose();
                return null;
            }
            outputCrop = new Crop(devices.DeviceContext);
            outputCropOutput = outputCrop.Output;
            outputTransform = new AffineTransform2D(devices.DeviceContext)
            {
                BorderMode = BorderMode.Hard,
            };
            outputTransform.SetInput(0, outputCropOutput, true);
            outputTransformOutput = outputTransform.Output;
            output = effect.Output;
            _interop = interop;
            _pipeline = pipeline;
            _effect = effect;
            _outputCrop = outputCrop;
            _outputCropOutput = outputCropOutput;
            _outputTransform = outputTransform;
            _outputTransformOutput = outputTransformOutput;
            disposer.Collect(effect);
            disposer.Collect(outputCrop);
            disposer.Collect(outputCropOutput);
            disposer.Collect(outputTransform);
            disposer.Collect(outputTransformOutput);
            disposer.Collect(output);
            return output;
        }
        catch
        {
            output?.Dispose();
            outputTransformOutput?.Dispose();
            outputTransform?.Dispose();
            outputCropOutput?.Dispose();
            outputCrop?.Dispose();
            effect?.Dispose();
            pipeline.Dispose();
            interop.Dispose();
            throw;
        }
    }

    protected override void setInput(ID2D1Image? inputImage)
    {
        _effect?.SetInput(0, inputImage, true);
        if (!_hasOutput)
            _effect?.SetInput(1, inputImage, true);
    }

    protected override void ClearEffectChain()
    {
        _effect?.SetInput(0, null, true);
        _effect?.SetInput(1, null, true);
        _outputCrop?.SetInput(0, null, true);
        _isFirst = true;
        _hasOutput = false;
        _hasOutputOffset = false;
        _hasCropRect = false;
        _hasRenderState = false;
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing)
            {
                ClearEffectChain();
                _interop?.WaitForIdle();
                _pipeline?.Dispose();
                _pipeline = null;
                _interop?.Dispose();
                _interop = null;
            }
        }
        finally
        {
            base.Dispose(disposing);
        }
    }

    private readonly record struct RenderState(LaplacianRemappingPipeline.PixelRect Rect);

    private readonly record struct Parameters(
        float Amount,
        float Detail,
        float Tone,
        float Threshold,
        LaplacianRemappingQuality Quality);
}
