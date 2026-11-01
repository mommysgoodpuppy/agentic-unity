// First-person tint while the horn glows: a soft haze of the magic colour at the edges of the
// wearer's view, growing with _Glow; the centre stays clear. A headset renders well past what
// its lenses show, so the haze starts close in. Drawn on the inside of a box around
// the eyes (Cull Front), over everything. It only draws for a camera within _Reach metres of the
// box's origin (in world space, so it doesn't depend on where or how big VRChat's first-person
// head shrinking leaves the box), and never in mirrors or VRChat's photo cameras (camera mode
// 1-3; the editor can report -1). The FX also enables the renderer only for the local player
// (IsLocal), and a VRCHeadChop keeps it full size in first person.
Shader "HornGlow/Vignette"
{
    Properties
    {
        _Glow ("Glow (animated)", Range(0, 1)) = 0
        _Hue ("Hue (animated by the menu)", Range(0, 1)) = 0.5
        _Color ("Tint Color (hue from _Hue)", Color) = (1, 0.25, 0.7, 1)
        _Strength ("Edge Opacity at Full Glow", Range(0, 1)) = 0.65
        _Inner ("Clear Radius (screen, 1 = edge)", Range(0, 1.5)) = 0.25
        _Outer ("Full Radius (screen, 1 = edge)", Range(0.3, 2)) = 0.8
        _Shimmer ("Shimmer", Range(0, 0.5)) = 0.15
        _Reach ("Camera Within (m, world)", Float) = 0.35
    }
    SubShader
    {
        Tags { "Queue"="Overlay" "RenderType"="Transparent" "IgnoreProjector"="True" "VRCFallback"="Hidden" }

        Pass
        {
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            // The camera is inside the box, so only its back faces cover the view, once each.
            Cull Front

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 4.5
            #include "UnityCG.cginc"

            float _Glow, _Hue, _Strength, _Inner, _Outer, _Shimmer, _Reach;
            float4 _Color;
            // Set globally by VRChat: non-zero in mirrors and in its handheld/screenshot cameras.
            float _VRChatMirrorMode, _VRChatCameraMode;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 clip : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                // This eye's camera (single-pass instanced stereo included) near the box's origin.
                float3 origin = mul(unity_ObjectToWorld, float4(0, 0, 0, 1)).xyz;
                bool near = distance(_WorldSpaceCameraPos, origin) < _Reach;
                bool show = _Glow > 0.001 && _VRChatMirrorMode == 0 && _VRChatCameraMode < 0.5 && near;
                o.pos = show ? UnityObjectToClipPos(v.vertex) : float4(0, 0, 0, 0);
                o.clip = o.pos;
                return o;
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
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                // Position on this eye's screen: 0 at the centre, 1 at the edge midpoints.
                float2 ndc = i.clip.xy / i.clip.w;
                float edge = smoothstep(_Inner, _Outer, length(ndc));
                float shimmer = 1 - _Shimmer + _Shimmer * sin(_Time.y * 1.7 + atan2(ndc.y, ndc.x) * 3);
                float a = saturate(edge * shimmer * _Strength * _Glow);
                return float4(WithHue(_Color.rgb, _Hue) * a, a);
            }
            ENDCG
        }
    }
}
