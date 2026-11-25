#ifndef LSBS_LIGHTING
#define LSBS_LIGHTING
// Grim, grimy lighting shared by soldiers and the world: no outlines, no bands, dirt over everything.

float Hash31(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.zyx + 31.32);
    return frac((p.x + p.y) * p.z);
}

float Noise3(float3 p)
{
    float3 i = floor(p), f = frac(p);
    f = f * f * (3 - 2 * f);
    return lerp(lerp(lerp(Hash31(i), Hash31(i + float3(1, 0, 0)), f.x), lerp(Hash31(i + float3(0, 1, 0)), Hash31(i + float3(1, 1, 0)), f.x), f.y),
                lerp(lerp(Hash31(i + float3(0, 0, 1)), Hash31(i + float3(1, 0, 1)), f.x), lerp(Hash31(i + float3(0, 1, 1)), Hash31(i + float3(1, 1, 1)), f.x), f.y), f.z);
}

/// kind: 0 cloth, earth, wood; 0.5 metal; 1 glowing. grime is sampled where the surface is, so it stays put on it.
half4 LSBSShade(half3 albedo, half kind, float3 positionWS, half3 N, float4 positionCS, float3 grimeAt, half mud, half fogFactor)
{
    half glow = smoothstep(0.75, 1.0, kind);
    half metal = smoothstep(0.25, 0.5, kind) * (1 - glow);

    half coarse = Noise3(grimeAt * 3.1), fine = Noise3(grimeAt * 17.0);
    half grime = coarse * 0.65 + fine * 0.35;
    albedo *= lerp(0.62, 1.22, grime);
    // rust and soot in patches on metal, mud from the ground up
    albedo = lerp(albedo, half3(0.085, 0.035, 0.015), metal * smoothstep(0.55, 0.8, grime) * 0.75);
    albedo = lerp(albedo, half3(0.062, 0.044, 0.028) * (0.7 + fine * 0.6), saturate(mud * (0.55 + coarse)));

    half3 V = normalize(GetWorldSpaceViewDir(positionWS));
    Light light = GetMainLight(TransformWorldToShadowCoord(positionWS));
    half fade = GetMainLightShadowFade(positionWS);
    half shadow = lerp(light.shadowAttenuation, 1, fade);
    half ndl = saturate(dot(N, light.direction));
    half wrap = saturate(dot(N, light.direction) * 0.6 + 0.4);

    half ao = 1;
    #if defined(_SCREEN_SPACE_OCCLUSION)
        AmbientOcclusionFactor aof = GetScreenSpaceAmbientOcclusion(GetNormalizedScreenSpaceUV(positionCS));
        ao = aof.indirectAmbientOcclusion;
    #endif

    half3 ambient = SampleSH(N) * ao;
    half3 color = albedo * (ambient + light.color * (ndl * 0.8 + wrap * 0.2) * shadow);

    half3 H = normalize(light.direction + V);
    half gloss = lerp(8, 46, metal) ;
    half spec = pow(saturate(dot(N, H)), gloss) * lerp(0.03, 0.9, metal) * (1 - grime * 0.8);
    color += light.color * spec * shadow * lerp(half3(1, 1, 1), albedo * 5, metal);
    // metal picks up the sky along its edges
    half rim = pow(1 - saturate(dot(N, V)), 4);
    color += metal * rim * SampleSH(reflect(-V, N)) * 0.35 * ao;

    color = lerp(color, albedo * 1.5, glow);
    color = MixFog(color, fogFactor);
    return half4(color, 1);
}
#endif
