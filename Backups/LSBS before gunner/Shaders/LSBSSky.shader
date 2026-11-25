// A low, smoke-stained sky with a smothered sun.
Shader "LSBS/Sky"
{
    Properties
    {
        _Horizon ("Horizon", Color) = (0.42, 0.36, 0.28, 1)
        _Zenith ("Zenith", Color) = (0.16, 0.165, 0.17, 1)
        _Sun ("Sun", Color) = (1, 0.62, 0.3, 1)
    }
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off ZWrite Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "LSBSLighting.hlsl"
            half4 _Horizon, _Zenith, _Sun;
            struct Varyings { float4 positionCS : SV_POSITION; float3 dir : TEXCOORD0; };
            Varyings vert(float4 v : POSITION)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.xyz);
                o.dir = v.xyz;
                return o;
            }
            half4 frag(Varyings i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float h = saturate(d.y);
                half3 c = lerp(_Horizon.rgb, _Zenith.rgb, pow(h, 0.45));
                // dragged-out smoke
                float2 p = d.xz / max(0.08, d.y + 0.12);
                float t = _Time.y * 0.012;
                float n = Noise3(float3(p * 0.9 + t, 1)) * 0.5 + Noise3(float3(p * 2.1 + t * 1.6, 4)) * 0.3 + Noise3(float3(p * 5.0 + t * 2.2, 9)) * 0.2;
                c = lerp(c, c * 0.5, smoothstep(0.42, 0.75, n) * smoothstep(0.0, 0.25, d.y));
                float s = saturate(dot(d, _MainLightPosition.xyz));
                c += _Sun.rgb * (pow(s, 6) * 0.16 + pow(s, 60) * 0.25 + smoothstep(0.9996, 0.9999, s) * 1.2) * (1 - 0.6 * n);
                return half4(c, 1);
            }
            ENDHLSL
        }
    }
}
