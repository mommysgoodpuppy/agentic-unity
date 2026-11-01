// Plain cartoon gold horn: the base material. Any opaque horn material can replace it
// (say, the gold of the horn it replaces). The glow is drawn on top by HornGlow/HornOverlay,
// and the aura around it is a separate object (HornGlow/Aura).
// UV0 samples _MainTex; UV1: u runs around the horn, v from base (0) to tip (1).
Shader "HornGlow/Horn"
{
    Properties
    {
        [Header(Horn)]
        _Color ("Base Color", Color) = (0.9, 0.72, 0.06, 1)
        _ShadeColor ("Shade Color", Color) = (0.8, 0.58, 0.05, 1)
        _MainTex ("Texture", 2D) = "white" {}
        _ShadeStep ("Shade Step", Range(-1, 1)) = 0.1
        _RimColor ("Rim Color", Color) = (1, 0.93, 0.6, 1)
        _RimPower ("Rim Power", Range(0.5, 8)) = 3
        _SpiralTurns ("Spiral Turns (per length at base)", Float) = 4
        _SpiralAccel ("Spiral Tightening", Float) = 0.2
        _SpiralLine ("Spiral Line Strength", Range(0, 1)) = 0.35
        _OutlineColor ("Outline Color", Color) = (0.16, 0.08, 0.2, 1)
        _OutlineWidth ("Outline Width (m)", Range(0, 0.003)) = 0.0007
    }

    CGINCLUDE
    #include "UnityCG.cginc"
    ENDCG

    SubShader
    {
        // Plain opaque; the aura draws later in the Transparent queue.
        Tags { "Queue"="Geometry" "RenderType"="Opaque" "IgnoreProjector"="True" "VRCFallback"="Toon" }

        Pass
        {
            Name "FORWARD"
            Tags { "LightMode"="ForwardBase" }
            ZWrite On
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #pragma multi_compile_instancing
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            sampler2D _MainTex; float4 _MainTex_ST;
            float4 _Color, _ShadeColor, _RimColor;
            float _ShadeStep, _RimPower, _SpiralTurns, _SpiralAccel, _SpiralLine;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 uv : TEXCOORD0; // xy: UV0, zw: UV1
                float3 wpos : TEXCOORD1;
                float3 wnormal : TEXCOORD2;
                float3 ambient : TEXCOORD3;
                SHADOW_COORDS(4)
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata_full v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = float4(v.texcoord.xy, v.texcoord1.xy);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.wnormal = UnityObjectToWorldNormal(v.normal);
                o.ambient = ShadeSH9(float4(0, 1, 0, 1));
                TRANSFER_SHADOW(o);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                // Per-eye camera position in single-pass (instanced) stereo.
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 n = normalize(i.wnormal);
                float3 v = normalize(_WorldSpaceCameraPos - i.wpos);
                float3 l = _WorldSpaceLightPos0.w > 0 ? normalize(_WorldSpaceLightPos0.xyz - i.wpos) : _WorldSpaceLightPos0.xyz;
                bool hasLight = dot(_LightColor0.rgb, 1) > 0.001;
                if (!hasLight) l = normalize(float3(0.3, 1, 0.4));
                float3 lightCol = hasLight ? _LightColor0.rgb : 0;
                // World lighting brightness, kept within a toon-friendly range.
                float3 light = saturate(lightCol + i.ambient);
                light = max(light, 0.15);

                float atten = SHADOW_ATTENUATION(i);
                float ndl = dot(n, l) * atten;
                float lit = smoothstep(_ShadeStep - 0.05, _ShadeStep + 0.05, ndl);
                float3 albedo = tex2D(_MainTex, TRANSFORM_TEX(i.uv.xy, _MainTex)).rgb;
                float3 baseCol = _Color.rgb * albedo;
                float3 shadeCol = _ShadeColor.rgb * albedo;
                float3 col = lerp(shadeCol, baseCol, lit) * light;

                // Dark spiral line along the groove.
                float spiral = smoothstep(0.75, 0.97, cos(6.2831853 * (i.uv.z - _SpiralTurns * (i.uv.w + _SpiralAccel * i.uv.w * i.uv.w)))) * (1 - i.uv.w);
                col = lerp(col, shadeCol * light * 0.8, spiral * _SpiralLine);

                float rim = pow(1 - saturate(dot(n, v)), _RimPower);
                col += _RimColor.rgb * rim * 0.35 * light;
                return float4(col, 1);
            }
            ENDCG
        }

        Pass
        {
            Name "OUTLINE"
            Tags { "LightMode"="ForwardBase" }
            Cull Front
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            float4 _OutlineColor; float _OutlineWidth;
            struct v2f { float4 pos : SV_POSITION; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert(appdata_full v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                wpos += normalize(UnityObjectToWorldNormal(v.normal)) * _OutlineWidth;
                o.pos = UnityWorldToClipPos(wpos);
                return o;
            }
            float4 frag(v2f i) : SV_Target { return _OutlineColor; }
            ENDCG
        }

        Pass
        {
            Name "SHADOWCASTER"
            Tags { "LightMode"="ShadowCaster" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_shadowcaster
            #pragma multi_compile_instancing
            struct v2f { V2F_SHADOW_CASTER; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert(appdata_base v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                TRANSFER_SHADOW_CASTER_NORMALOFFSET(o)
                return o;
            }
            float4 frag(v2f i) : SV_Target { SHADOW_CASTER_FRAGMENT(i) }
            ENDCG
        }
    }
    FallBack Off
}
