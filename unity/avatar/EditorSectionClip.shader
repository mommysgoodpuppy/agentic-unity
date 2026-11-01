Shader "Hidden/VRUnity/EditorSectionClip"
{
 Properties { _MainTex("Texture",2D)="white" {} _Color("Color",Color)=(1,1,1,1) }
 SubShader
 {
  Tags { "RenderType"="Opaque" "Queue"="Geometry" }
  Pass
  {
   Cull Off
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex; float4 _MainTex_ST,_Color,_VRUnitySectionPlane;
   struct input { float4 vertex:POSITION; float3 normal:NORMAL; float2 uv:TEXCOORD0; };
   struct output { float4 position:SV_POSITION; float3 world:TEXCOORD0; float2 uv:TEXCOORD1; float3 normal:TEXCOORD2; };
   output vert(input v) { output o; o.position=UnityObjectToClipPos(v.vertex); o.world=mul(unity_ObjectToWorld,v.vertex).xyz; o.uv=TRANSFORM_TEX(v.uv,_MainTex); o.normal=UnityObjectToWorldNormal(v.normal); return o; }
   float4 frag(output i):SV_Target
   {
    clip(dot(float4(i.world,1),_VRUnitySectionPlane));
    float3 color=tex2D(_MainTex,i.uv).rgb*_Color.rgb;
    float light=.45+.55*abs(dot(normalize(i.normal),normalize(float3(.3,.8,.6))));
    return float4(color*light,1);
   }
   ENDCG
  }
 }
}
