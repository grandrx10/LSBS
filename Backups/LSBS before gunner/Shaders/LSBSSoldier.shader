// Every soldier, drawn in a handful of instanced calls: poses come from a baked table, corpses bring their own.
Shader "LSBS/Soldier"
{
    Properties { }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        struct Instance
        {
            float3 pos; float yaw;
            uint4 frames;   // a, a next, b, b next
            float4 blends;  // within a, within b, b to a, pitch
            float4 misc;    // seed, flags (1 captain, 2 no head, 4 the player's squad, 8 sweeper, 16 communicator), corpse or -1, height
            float4 tilt;    // up, for somebody fallen on a slope
        };
        StructuredBuffer<Instance> _Instances;
        StructuredBuffer<float4> _Poses;
        StructuredBuffer<float4> _Corpses;
        StructuredBuffer<float> _Upper;
        int _BoneCount, _ChestBone, _HeadBone, _HelmetBone;
        float3 _ChestPivot;

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS   : NORMAL;
            float4 color      : COLOR;
            float4 bones      : TEXCOORD1;
            uint id           : SV_InstanceID;
        };

        struct Skinned { float3 pos; float3 nrm; float3 local; float4 misc; };

        float3x4 Pose(uint frame, uint bone)
        {
            uint k = (frame * _BoneCount + bone) * 3;
            return float3x4(_Poses[k], _Poses[k + 1], _Poses[k + 2]);
        }

        float3x4 Animated(Instance i, uint bone)
        {
            float3x4 a = Pose(i.frames.x, bone) * (1 - i.blends.x) + Pose(i.frames.y, bone) * i.blends.x;
            if (i.blends.z < 0.999)
            {
                float3x4 b = Pose(i.frames.z, bone) * (1 - i.blends.y) + Pose(i.frames.w, bone) * i.blends.y;
                a = b * (1 - i.blends.z) + a * i.blends.z;
            }
            return a;
        }

        float3x4 Corpse(uint c, uint bone)
        {
            uint k = (c * _BoneCount + bone) * 3;
            return float3x4(_Corpses[k], _Corpses[k + 1], _Corpses[k + 2]);
        }

        Skinned Skin(Attributes v)
        {
            Instance i = _Instances[v.id];
            uint flags = (uint)i.misc.y;
            uint b0 = (uint)v.bones.x, b1 = (uint)v.bones.y;
            float w0 = v.bones.z;
            Skinned o;
            o.misc = i.misc;
            o.local = v.positionOS.xyz;
            float3x4 m;
            if (i.misc.z >= 0)
            {
                uint c = (uint)i.misc.z;
                m = Corpse(c, b0) * w0;
                if (w0 < 0.999) m += Corpse(c, b1) * (1 - w0);
                o.pos = mul(m, float4(v.positionOS.xyz, 1)) + i.pos; // pos: how far they've dropped since
                o.nrm = normalize(mul((float3x3)m, v.normalOS));
            }
            else
            {
                m = Animated(i, b0) * w0;
                if (w0 < 0.999) m += Animated(i, b1) * (1 - w0);
                float3 p = mul(m, float4(v.positionOS.xyz, 1));
                float3 n = mul((float3x3)m, v.normalOS);
                // the aim bends the body from the chest up
                float up = _Upper[b0] * w0 + _Upper[b1] * (1 - w0);
                if (abs(i.blends.w) > 0.001 && up > 0)
                {
                    float3 pivot = mul(Animated(i, _ChestBone), float4(_ChestPivot, 1));
                    float s, c; sincos(i.blends.w * up, s, c);
                    float3 d = p - pivot;
                    p = pivot + float3(d.x, d.y * c - d.z * s, d.y * s + d.z * c);
                    n = float3(n.x, n.y * c - n.z * s, n.y * s + n.z * c);
                }
                p *= i.misc.w;
                float sy, cy; sincos(i.yaw, sy, cy);
                p = float3(p.x * cy + p.z * sy, p.y, -p.x * sy + p.z * cy);
                n = float3(n.x * cy + n.z * sy, n.y, -n.x * sy + n.z * cy);
                if (i.tilt.y < 0.9999)
                {
                    // lean from upright to that up
                    float3 axis = float3(i.tilt.z, 0, -i.tilt.x);
                    float c = i.tilt.y, k = 1 / (1 + c);
                    p = p * c + cross(axis, p) + axis * dot(axis, p) * k;
                    n = n * c + cross(axis, n) + axis * dot(axis, n) * k;
                }
                o.pos = p + i.pos;
                o.nrm = normalize(n);
            }
            // who each part's for: 1 captains, 2 not communicators, 3 communicators, 4 sweepers, 5 not sweepers
            uint who = (uint)(v.bones.w + 0.5);
            bool hide = (who == 1 && (flags & 1) == 0) || (who == 2 && (flags & 16) != 0) || (who == 3 && (flags & 16) == 0)
                     || (who == 4 && (flags & 8) == 0) || (who == 5 && (flags & 8) != 0)
                     || ((flags & 2) != 0 && (b0 == (uint)_HeadBone || b0 == (uint)_HelmetBone));
            if (hide) o.pos = asfloat(0x7fc00000); // not a number: the triangle is dropped
            return o;
        }
        ENDHLSL

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "LSBSLighting.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float4 color      : TEXCOORD2;
                float4 local      : TEXCOORD3; // where on the body, fog
                nointerpolation float4 misc : TEXCOORD4;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                Skinned s = Skin(v);
                o.positionWS = s.pos;
                o.normalWS = s.nrm;
                o.positionCS = TransformWorldToHClip(s.pos);
                o.color = v.color;
                o.local = float4(s.local, ComputeFogFactor(o.positionCS.z));
                o.misc = s.misc;
                return o;
            }

            half4 frag(Varyings i, bool front : SV_IsFrontFace) : SV_Target
            {
                half3 N = normalize(i.normalWS);
                float3 at = i.local.xyz + i.misc.x * 17.0;
                half shade = lerp(0.8, 1.15, frac(i.misc.x * 7.31));
                // mud creeps up from the boots (bind pose: z is up the body in the model's own space)
                half mud = saturate(0.35 - i.local.z * 0.22) * 0.8;
                half4 c = LSBSShade(i.color.rgb * shade, i.color.a, i.positionWS, N, i.positionCS, at, mud, i.local.w);
                uint flags = (uint)i.misc.y;
                if (flags & 4)
                {
                    half3 V = normalize(GetWorldSpaceViewDir(i.positionWS));
                    half rim = pow(1 - saturate(dot(N, V)), 1.6);
                    c.rgb += half3(1.0, 0.62, 0.16) * (rim * 0.16 + 0.006);
                }
                return c;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ColorMask 0
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;
            float4 vert(Attributes v) : SV_POSITION
            {
                Skinned s = Skin(v);
                float4 p = TransformWorldToHClip(ApplyShadowBias(s.pos, s.nrm, _LightDirection));
                #if UNITY_REVERSED_Z
                    p.z = min(p.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    p.z = max(p.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return p;
            }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ColorMask R
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 vert(Attributes v) : SV_POSITION { return TransformWorldToHClip(Skin(v).pos); }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; };
            Varyings vert(Attributes v)
            {
                Varyings o;
                Skinned s = Skin(v);
                o.positionCS = TransformWorldToHClip(s.pos);
                o.normalWS = s.nrm;
                return o;
            }
            half4 frag(Varyings i) : SV_Target { return half4(normalize(i.normalWS), 0); }
            ENDHLSL
        }
    }
}
