#ifndef AARIS_PSEUDO_SUBSURFACE_INCLUDED
#define AARIS_PSEUDO_SUBSURFACE_INCLUDED
// Half precision wrapped Lambert, no extra shadow sampling, acos or pow.
void PseudoSubsurface_half(half3 WorldPosition, half3 WorldNormal,
    half3 SSRadius, half ShadowResponse, out half3 ssAmount)
{
#ifdef SHADERGRAPH_PREVIEW
    half3 sunColor = half3(1,1,1);
    half3 sunDir = normalize(half3(0.7,0.6,0.4));
#else
    Light sun = GetMainLight();
    half3 sunColor = sun.color;
    half3 sunDir = sun.direction;
#endif
    half3 wrap = saturate(SSRadius);
    half backFacing = dot(normalize(WorldNormal), -sunDir);
    half3 lightWrap = saturate((backFacing + wrap) / (1.0h + wrap));
    lightWrap = lightWrap * lightWrap * (3.0h - 2.0h * lightWrap);
    // ShadowResponse was historically used for extra shadow-map reads;
    // do not repeat that work for decorative transmission.
    ssAmount = sunColor * lightWrap;
}
#endif
