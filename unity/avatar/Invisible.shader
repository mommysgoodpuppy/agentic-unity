// Draws nothing: no colour, no depth. For mesh parts that should vanish without editing the
// mesh (glasses lenses, say). VRCFallback=Hidden keeps them invisible when VRChat's
// safety settings replace custom shaders.
Shader "VRUnity/Invisible"
{
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "VRCFallback"="Hidden" }
        Pass
        {
            ColorMask 0
            ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct v2f { float4 pos : SV_POSITION; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert(appdata_base v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }
            fixed4 frag(v2f i) : SV_Target { return 0; }
            ENDCG
        }
    }
}
