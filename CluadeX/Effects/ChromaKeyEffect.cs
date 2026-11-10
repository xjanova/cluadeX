using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace CluadeX.Effects;

/// <summary>
/// Takes the flat green out of น้อง CluadeX's clips and fades her edges like the still picture (ChromaKey.fx; the compiled
/// ChromaKey.ps is the resource). WPF cannot play video with an alpha channel, so the clips are filmed on green and keyed
/// here, on the GPU (or WPF's software path, which runs ps_2_0 too).
/// </summary>
public sealed class ChromaKeyEffect : ShaderEffect
{
    private static readonly PixelShader Shader = new()
    {
        UriSource = new Uri("pack://application:,,,/CluadeX;component/Effects/ChromaKey.ps", UriKind.Absolute),
    };

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty(nameof(Input), typeof(ChromaKeyEffect), 0);

    /// <summary>x: green over red/blue where she starts to go see-through, y: how much more until she is gone, z: the
    /// most green a kept pixel may stand above its red/blue (spill), w: the choke — a neighbour greener than x + w lets the
    /// pixel go, so the grey-green rim where she meets the green is cut.</summary>
    public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(nameof(Key), typeof(Point4D),
        typeof(ChromaKeyEffect), new UIPropertyMetadata(new Point4D(0.10, 0.12, 0.02, 0.10), PixelShaderConstantCallback(0)));

    /// <summary>x: bottom, y: sides, z: top — the share of each side that fades out (the still: 0.20, 0.10, 0.03).</summary>
    public static readonly DependencyProperty FadeProperty = DependencyProperty.Register(nameof(Fade), typeof(Point4D),
        typeof(ChromaKeyEffect), new UIPropertyMetadata(new Point4D(0.20, 0.10, 0.03, 0), PixelShaderConstantCallback(1)));

    /// <summary>x, y: how far the choke looks, as a share of the clip: four of its 240×480 pixels. Windows' own video
    /// decoder (Media Foundation, what MediaElement plays through) blurs her edge into the green over 3-4 pixels, wider than
    /// ffmpeg shows it, with the start picture's white halo just inside; two pixels left a grey-green rim.</summary>
    public static readonly DependencyProperty TexelProperty = DependencyProperty.Register(nameof(Texel), typeof(Point4D),
        typeof(ChromaKeyEffect), new UIPropertyMetadata(new Point4D(4.0 / 240, 4.0 / 480, 0, 0), PixelShaderConstantCallback(2)));

    public ChromaKeyEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(KeyProperty);
        UpdateShaderValue(FadeProperty);
        UpdateShaderValue(TexelProperty);
    }

    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public Point4D Key
    {
        get => (Point4D)GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    public Point4D Fade
    {
        get => (Point4D)GetValue(FadeProperty);
        set => SetValue(FadeProperty, value);
    }

    public Point4D Texel
    {
        get => (Point4D)GetValue(TexelProperty);
        set => SetValue(TexelProperty, value);
    }
}
