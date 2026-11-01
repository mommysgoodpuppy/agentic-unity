// Diagnostic only, for comparing an avatar before and after optimization.
// _Mode 0: front faces flat grey shaded by the normal; back faces magenta. Magenta seen from outside
//          means a hole or a flipped face.
// _Mode 1: world normal as colour, front faces only.
// Front and back are separate passes using culling (not VFACE, whose sign flips with render-texture
// orientation on some APIs); depth testing hides back faces that sit behind front faces.
Shader "Hidden/VRUnity/FaceCheck"
{
    Properties { _Mode ("Mode", Float) = 0 }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        CGINCLUDE
        #include "UnityCG.cginc"
        float _Mode;
        struct v2f { float4 pos : SV_POSITION; float3 n : TEXCOORD0; };
        v2f vert(appdata_base v)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(v.vertex);
            o.n = UnityObjectToWorldNormal(v.normal);
            return o;
        }
        ENDCG
        Pass
        {
            Cull Back
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.n);
                if (_Mode > 0.5) return float4(n * 0.5 + 0.5, 1);
                float l = 0.35 + 0.55 * saturate(dot(n, normalize(float3(0.4, 0.7, 0.6))));
                return float4(l, l, l, 1);
            }
            ENDCG
        }
        Pass
        {
            Cull Front
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                if (_Mode > 0.5) discard;
                return float4(1, 0, 1, 1);
            }
            ENDCG
        }
    }
}
