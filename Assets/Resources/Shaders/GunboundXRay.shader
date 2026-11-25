// See-through edge outline: a thin line around the mesh's silhouette, drawn only where something is in front
// of it. The inside is masked out by Gunbound/XRayMask (stencil), so only the edge shows.
// Used on the King of the Hill point so you can always find it.
Shader "Gunbound/XRay"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 0.9)
        _Width ("Width (pixels)", Float) = 2.5
        _CenterOS ("Centre (object space)", Vector) = (0, 0, 0, 0)
    }
    SubShader
    {
        Tags { "Queue"="Transparent+51" "RenderType"="Transparent" "IgnoreProjector"="True" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "XRayOutline"
            Tags { "LightMode"="SRPDefaultUnlit" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Greater
            Cull Off
            Stencil { Ref 64 ReadMask 64 Comp NotEqual }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Width;
                float4 _CenterOS;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert (Attributes v)
            {
                Varyings o;
                float4 cs = TransformObjectToHClip(v.positionOS.xyz);
                float4 cc = TransformObjectToHClip(_CenterOS.xyz);
                // push every vertex away from the mesh centre on screen: a fat silhouette (convex shapes like a cylinder)
                float2 px = (cs.xy / cs.w - cc.xy / cc.w) * _ScreenParams.xy * 0.5; // centre -> vertex, in pixels
                float2 dir = px / max(length(px), 1e-4);
                cs.xy += dir * (_Width * 2.0 / _ScreenParams.xy) * cs.w;            // _Width pixels further out
                o.positionCS = cs;
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                return _Color;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
