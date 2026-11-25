// Transparent, vertex-coloured, unlit — like Sprites/Default, but fades out close to the camera.
// Used for the local player's own speed trail so it never blocks their view.
Shader "Gunbound/NearFade"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _FadeStart ("Invisible within (m)", Float) = 2
        _FadeEnd ("Fully visible beyond (m)", Float) = 3.5
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _FadeStart, _FadeEnd;

            struct appdata { float4 vertex : POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; float3 wpos : TEXCOORD1; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * i.color;
                float d = distance(i.wpos, _WorldSpaceCameraPos);
                c.a *= saturate((d - _FadeStart) / max(0.001, _FadeEnd - _FadeStart));
                return c;
            }
            ENDCG
        }
    }
    Fallback Off
}
