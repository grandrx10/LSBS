// Smoke, steam, dirt, muzzle flashes and tracers: camera-facing cards, all in one instanced call.
Shader "LSBS/Fx"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }
        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            Blend One OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            struct Puff { float3 pos; float size; float3 dir; float length; float4 color; float seed; float additive; float shape; float pad; };
            StructuredBuffer<Puff> _Puffs;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : TEXCOORD1;
                float4 data : TEXCOORD2; // seed, additive, shape, fog
                float eye : TEXCOORD3;
            };

            float Hash21(float2 p) { p = frac(p * float2(123.34, 456.21)); p += dot(p, p + 45.32); return frac(p.x * p.y); }
            float Noise2(float2 p)
            {
                float2 i = floor(p), f = frac(p); f = f * f * (3 - 2 * f);
                return lerp(lerp(Hash21(i), Hash21(i + float2(1, 0)), f.x), lerp(Hash21(i + float2(0, 1)), Hash21(i + float2(1, 1)), f.x), f.y);
            }

            Varyings vert(uint vid : SV_VertexID, uint id : SV_InstanceID)
            {
                static const float2 corners[6] = { float2(-1, -1), float2(-1, 1), float2(1, 1), float2(-1, -1), float2(1, 1), float2(1, -1) };
                Puff p = _Puffs[id];
                float2 c = corners[vid % 6];
                float3 right = UNITY_MATRIX_V[0].xyz, up = UNITY_MATRIX_V[1].xyz;
                float3 w;
                if (p.length > 0)
                {
                    // a streak along dir
                    float3 mid = p.pos + p.dir * p.length * 0.5;
                    float3 side = normalize(cross(p.dir, _WorldSpaceCameraPos - mid) + 1e-5);
                    // never thinner than about a pixel
                    float thick = max(p.size, 0.0005 * length(_WorldSpaceCameraPos - mid));
                    w = mid + p.dir * (c.y * p.length * 0.5) + side * c.x * thick;
                }
                else
                {
                    float s, k; sincos(p.seed * 6.28, s, k);
                    float2 r = float2(c.x * k - c.y * s, c.x * s + c.y * k);
                    w = p.pos + (right * r.x + up * r.y) * p.size;
                }
                Varyings o;
                o.positionCS = TransformWorldToHClip(w);
                o.uv = c;
                o.color = p.color;
                o.data = float4(p.seed, p.additive, p.shape, ComputeFogFactor(o.positionCS.z));
                o.eye = -TransformWorldToView(w).z;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float d = length(i.uv);
                float a;
                if (i.data.z > 1.5) a = (1 - abs(i.uv.x)) * smoothstep(1, 0.3, abs(i.uv.y));            // streak
                else if (i.data.z > 0.5)                                                                // flash
                {
                    float ang = atan2(i.uv.y, i.uv.x);
                    float spikes = 0.55 + 0.45 * cos(ang * 5 + i.data.x * 9);
                    a = saturate(1 - d / (0.35 + 0.6 * spikes));
                    a *= a;
                    a += saturate(1 - d * 3) ;
                }
                else                                                                                    // smoke
                {
                    float n = Noise2(i.uv * 1.7 + i.data.x * 31) * 0.6 + Noise2(i.uv * 4.1 + i.data.x * 57) * 0.4;
                    a = smoothstep(1.0, 0.15, d + (n - 0.5) * 0.7);
                    a *= a * (0.6 + 0.4 * n);
                }
                float2 uv = i.positionCS.xy / _ScaledScreenParams.xy;
                float scene = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
                if (i.data.z < 0.5) a *= saturate((scene - i.eye) * 1.2) * saturate(i.eye - 0.4);
                else if (i.data.z < 1.5) a *= saturate((scene - i.eye + 0.25) * 2.5);
                a *= i.color.a;
                half3 rgb = i.color.rgb;
                rgb = lerp(MixFog(rgb, i.data.w), rgb, i.data.y);
                return half4(rgb * a, a * (1 - i.data.y));
            }
            ENDHLSL
        }
    }
}
