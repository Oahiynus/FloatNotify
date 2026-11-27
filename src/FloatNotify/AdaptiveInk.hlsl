// Transparent WPF controls remain independently interactive. Only recolor their rendered ink.
sampler2D controls : register(s0);
sampler2D illumination : register(s1);
float dayGain : register(c0);

float4 main(float2 uv : TEXCOORD0) : COLOR0
{
    float4 src = tex2D(controls, uv);
    float3 rgb = src.rgb / max(src.a, 0.001);
    float luma = dot(rgb, float3(0.2126, 0.7152, 0.0722));
    float3 lightInk = saturate(1.14 - 1.20 * luma + 0.40 * (rgb - luma));
    float4 mask = tex2D(illumination, uv);
    float useLight = saturate((mask.r - 0.47) / 0.06);
#ifdef SOFT_SELECTION
    // Blend after the ink switch so a weak row correction cannot flip the entire fill.
    useLight += (1.0 - useLight) * mask.g;
#endif
    return float4(lerp(rgb * dayGain, lightInk, useLight) * src.a, src.a);
}
