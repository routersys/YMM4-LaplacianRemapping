using System.ComponentModel.DataAnnotations;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Effects;

namespace LaplacianRemapping;

[VideoEffect(nameof(Texts.LaplacianRemapping), [VideoEffectCategories.Filtering], [nameof(Texts.TagClarity), nameof(Texts.TagTone), nameof(Texts.TagDetail)], IsAviUtlSupported = false, ResourceType = typeof(Texts))]
public sealed class LaplacianRemappingEffect : VideoEffectBase
{
    public override string Label => Texts.LaplacianRemapping;

    public LaplacianRemappingEffect()
    {
        LaplacianRemappingUpdateNotifier.EnsureCheckedOnce();
    }

    [Display(GroupName = nameof(Texts.BasicGroup), Name = nameof(Texts.Amount), Description = nameof(Texts.AmountDescription), Order = 0, ResourceType = typeof(Texts))]
    [AnimationSlider("F1", "%", 0, 100)]
    public Animation Amount { get; } = new Animation(100, 0, 100);

    [Display(GroupName = nameof(Texts.BasicGroup), Name = nameof(Texts.Quality), Description = nameof(Texts.QualityDescription), Order = 1, ResourceType = typeof(Texts))]
    [EnumComboBox]
    public LaplacianRemappingQuality Quality { get => _quality; set => Set(ref _quality, value); }
    private LaplacianRemappingQuality _quality = LaplacianRemappingQuality.High;

    [Display(GroupName = nameof(Texts.AdjustGroup), Name = nameof(Texts.Detail), Description = nameof(Texts.DetailDescription), Order = 10, ResourceType = typeof(Texts))]
    [AnimationSlider("F1", "%", -100, 100)]
    public Animation Detail { get; } = new Animation(50, -100, 100);

    [Display(GroupName = nameof(Texts.AdjustGroup), Name = nameof(Texts.Tone), Description = nameof(Texts.ToneDescription), Order = 11, ResourceType = typeof(Texts))]
    [AnimationSlider("F1", "%", -100, 100)]
    public Animation Tone { get; } = new Animation(0, -100, 100);

    [Display(GroupName = nameof(Texts.AdjustGroup), Name = nameof(Texts.Threshold), Description = nameof(Texts.ThresholdDescription), Order = 12, ResourceType = typeof(Texts))]
    [AnimationSlider("F1", "%", 1, 100)]
    public Animation Threshold { get; } = new Animation(30, 1, 100);

    private IAnimatable[]? _animatables;

    public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription) => [];

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new LaplacianRemappingEffectProcessor(devices, this);

    protected override IEnumerable<IAnimatable> GetAnimatables()
        => _animatables ??= [Amount, Detail, Tone, Threshold];
}
