using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace FloatNotify;

internal sealed class AdaptiveInk : ShaderEffect
{
    static readonly PixelShader Shader=LoadShader("AdaptiveInk");
    static readonly Lazy<PixelShader> SoftSelection=new(()=>LoadShader("SoftSelection"));
    static readonly DependencyProperty InputProperty=RegisterPixelShaderSamplerProperty("Input",typeof(AdaptiveInk),0);
    static readonly DependencyProperty MaskProperty=RegisterPixelShaderSamplerProperty("Mask",typeof(AdaptiveInk),1,SamplingMode.Bilinear);

    static readonly DependencyProperty DayGainProperty=DependencyProperty.Register("DayGain",typeof(double),typeof(AdaptiveInk),
        new UIPropertyMetadata(0.90,PixelShaderConstantCallback(0)));
    static readonly ImageBrush DarkFallback=new(BitmapSource.Create(1,1,96,96,PixelFormats.Gray8,null,new byte[]{255},1));
    internal AdaptiveInk(bool preserveDayInk,bool softSelection=false){
        PixelShader=softSelection?SoftSelection.Value:Shader;
        if(preserveDayInk)SetValue(DayGainProperty,1.0);
        UpdateShaderValue(DayGainProperty);
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(MaskProperty);
    }
    internal void SetMask(ImageBrush? mask)=>SetValue(MaskProperty,mask??DarkFallback);

    static PixelShader LoadShader(string name){
        using var stream=typeof(AdaptiveInk).Assembly.GetManifestResourceStream($"FloatNotify.{name}.ps")
            ?? throw new InvalidOperationException("缺少自适应玻璃着色器。");
        var shader=new PixelShader();
        shader.SetStreamSource(stream);
        shader.Freeze();
        return shader;
    }
}
