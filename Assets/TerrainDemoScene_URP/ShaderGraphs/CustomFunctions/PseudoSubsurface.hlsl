// Mobile foliage transmission: inexpensive wrapped backlight + soft sun-side rim.
// No pow/acos, additional texture fetch, realtime shadow sample or render pass.
// Existing ShaderGraph inputs/outputs remain unchanged.
void PseudoSubsurface_half(float3 WorldPosition, half3 WorldNormal, half3 SSRadius,
                           half ShadowResponse, out half3 ssAmount)
{
#ifdef SHADERGRAPH_PREVIEW
    half3 color = half3(0.0h, 0.0h, 0.0h);
    half3 lightDir = normalize(half3(0.707h, 0.5h, 0.707h));
#else
    // Directional lighting only; geometry occlusion stays in the regular URP
    // material lighting path rather than triggering a second shadow lookup.
    Light mainLight = GetMainLight();
    half3 color = mainLight.color;
    half3 lightDir = mainLight.direction;
#endif
    half3 n = normalize(WorldNormal);
    half radius = saturate(SSRadius.x);
    half back = saturate((dot(n, -lightDir) + radius) / (1.0h + radius));
    half wrapped = back * back * (3.0h - 2.0h * back);
    half edge = 1.0h - abs(dot(n, lightDir));
    half rim = edge * edge;
    half skyFill = 0.085h * saturate(n.y * 0.5h + 0.5h);
    half transmission = 0.80h * wrapped + 0.12h * rim + skyFill;
    // Keep indirect translucency visible in shade without inventing emission.
    half response = 1.0h - 0.12h * saturate(ShadowResponse);
    ssAmount = max(half3(0.0h, 0.0h, 0.0h), color * (transmission * response));
}
