// The horn's magic tint, drawn over whatever opaque material the horn has (a second
// material slot on the same renderer). _Glow (0..1) is driven by the HornGlow animator:
// at 0 it draws nothing; toward 1 the horn turns pink with white spiral highlights and
// shimmers, brightest at the tip.
// UV1: u runs around the horn, v from base (0) to tip (1). UV0 belongs to the base material.
Shader "HornGlow/HornOverlay"
{
    Properties
    {
        _Glow ("Glow (animated)", Range(0, 1)) = 0
        _Hue ("Hue (animated by the menu)", Range(0, 1)) = 0.5
        _MagicColor ("Magic Color (hue from _Hue)", Color) = (1, 0.22, 0.68, 1)
        _MagicCore ("Magic Core Color (hue from _Hue)", Color) = (1, 0.8, 0.95, 1)
        _Tint ("Tint Amount", Range(0, 1)) = 0.6
        _Emission ("Emission", Range(0, 4)) = 0.6
        _SpiralTurns ("Spiral Turns (per length at base)", Float) = 4
        _SpiralAccel ("Spiral Tightening", Float) = 0.2
        _SpiralGlow ("Spiral Highlight", Range(0, 1)) = 0.8
        _FlickerSpeed ("Shimmer Speed", Range(0, 10)) = 2.5
        _FlickerScale ("Shimmer Noise Scale", Range(1, 400)) = 60
    }

    SubShader
    {
        // Right after the opaque horn, before the aura (Transparent).
        Tags { "Queue"="Geometry+1" "RenderType"="Transparent" "IgnoreProjector"="True" "VRCFallback"="Hidden" }

        Pass
        {
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            // Same surface as the base material; nudge forward so it never z-fights.
            Offset -1, -1

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            float _Glow, _Hue, _Tint, _Emission, _SpiralTurns, _SpiralAccel, _SpiralGlow, _FlickerSpeed, _FlickerScale;
            float4 _MagicColor, _MagicCore;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 wpos : TEXCOORD1;
                float3 wnormal : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata_full v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = _Glow < 0.001 ? float4(0, 0, 0, 0) : UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord1.xy;
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.wnormal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            float hash31(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.zyx + 31.32);
                return frac((p.x + p.y) * p.z);
            }

            float valueNoise(float3 p)
            {
                float3 i = floor(p), f = frac(p);
                f = f * f * (3 - 2 * f);
                return lerp(
                    lerp(lerp(hash31(i), hash31(i + float3(1, 0, 0)), f.x),
                         lerp(hash31(i + float3(0, 1, 0)), hash31(i + float3(1, 1, 0)), f.x), f.y),
                    lerp(lerp(hash31(i + float3(0, 0, 1)), hash31(i + float3(1, 0, 1)), f.x),
                         lerp(hash31(i + float3(0, 1, 1)), hash31(i + float3(1, 1, 1)), f.x), f.y), f.z);
            }

            // The colour with its hue replaced; saturation and brightness are kept.
            float3 WithHue(float3 c, float hue)
            {
                float hi = max(c.r, max(c.g, c.b)), lo = min(c.r, min(c.g, c.b));
                float3 pure = saturate(abs(frac(hue + float3(1, 2.0 / 3, 1.0 / 3)) * 6 - 3) - 1);
                return lo + (hi - lo) * pure;
            }

            float4 frag(v2f i) : SV_Target
            {
                // Per-eye camera position in single-pass (instanced) stereo.
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 magic = WithHue(_MagicColor.rgb, _Hue), core = WithHue(_MagicCore.rgb, _Hue);
                float3 n = normalize(i.wnormal);
                float3 v = normalize(_WorldSpaceCameraPos - i.wpos);
                float rim = pow(1 - saturate(dot(n, v)), 3);
                float tip = saturate(i.uv.y);
                float spiral = smoothstep(0.75, 0.97, cos(6.2831853 * (i.uv.x - _SpiralTurns * (i.uv.y + _SpiralAccel * i.uv.y * i.uv.y)))) * (1 - tip);
                float shimmer = 0.75 + 0.25 * valueNoise(i.wpos * _FlickerScale - float3(0, _Time.y * _FlickerSpeed, 0));

                // A pink veil over the base material, white along the spiral.
                float3 tint = lerp(lerp(magic, 1, 0.3), 1, spiral);
                float alpha = saturate(_Tint + spiral * _SpiralGlow) * _Glow;
                // Plus light from inside, brightest toward the tip; doesn't hide what's under it.
                float3 glow = lerp(magic, core, rim * 0.5 + tip * 0.4)
                              * _Glow * _Emission * (0.35 + 0.65 * tip) * shimmer;
                return float4(tint * alpha + glow, alpha);
            }
            ENDCG
        }
    }
    FallBack Off
}
