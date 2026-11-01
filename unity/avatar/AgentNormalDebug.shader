// Diagnostic only: world normal (or tangent with _ShowTangent 1) as colour.
Shader "Hidden/VRUnity/NormalDebug"
{
    Properties { _ShowTangent ("Show Tangent", Float) = 0 }
    SubShader
    {
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float _ShowTangent;
            struct v2f { float4 pos : SV_POSITION; float3 n : TEXCOORD0; float3 t : TEXCOORD1; };
            v2f vert(appdata_full v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.n = UnityObjectToWorldNormal(v.normal);
                o.t = UnityObjectToWorldDir(v.tangent.xyz) * v.tangent.w;
                return o;
            }
            float4 frag(v2f i) : SV_Target
            {
                float3 d = _ShowTangent > 0.5 ? i.t : normalize(i.n);
                return float4(d * 0.5 + 0.5, 1);
            }
            ENDCG
        }
    }
}
