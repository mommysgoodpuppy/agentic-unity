// Volumetric magic aura around the horn: a lumpy pill along the horn, ray-marched inside
// a box mesh, tapering toward the tip. See-through in the middle, more opaque toward its
// silhouette, with four-ray sparkles twinkling in place inside.
// It writes the depth of its surface, so where it goes into the head it is cut off cleanly.
// Shape properties are in the object space of the Aura object: the horn's base at the
// origin, pointing along +Y. _BoxMin/_BoxMax must match the mesh's bounds.
Shader "HornGlow/Aura"
{
    Properties
    {
        _Glow ("Glow (animated)", Range(0, 1)) = 0

        [Header(Colour)]
        _Hue ("Hue (animated by the menu)", Range(0, 1)) = 0.5
        _Color ("Magic Color (hue from _Hue)", Color) = (1, 0.25, 0.7, 1)
        _EdgeColor ("Edge Color (hue from _Hue)", Color) = (1, 0.45, 0.8, 1)
        _CenterAlpha ("Center Opacity", Range(0, 1)) = 0.38
        _EdgeAlpha ("Edge Opacity", Range(0, 1)) = 0.85
        _EdgePower ("Edge Falloff", Range(0.5, 8)) = 2.5
        _Feather ("Soft Rim Width (m)", Range(0, 0.02)) = 0.003

        [Header(Shape)]
        _PillA ("Pill Start", Vector) = (0, 0, 0, 0)
        _PillB ("Pill End", Vector) = (0, 0.082, 0, 0)
        _PillRadius ("Pill Base Radius (m)", Range(0, 0.08)) = 0.036
        _PillTipRadius ("Pill Tip Radius (m)", Range(0, 0.08)) = 0.014
        _PillFlare ("Base Flare (1 = straight taper)", Range(1, 3)) = 1.5
        _GrowFrom ("Size While Fading In", Range(0, 1)) = 0.6
        _LumpAmount ("Lump Size (m)", Range(0, 0.02)) = 0.009
        _LumpScale ("Lump Frequency", Range(1, 300)) = 60
        _LumpSpeed ("Lump Wobble Speed", Range(0, 5)) = 0.5

        [Header(Second Outline)]
        _Outline2Alpha ("Opacity", Range(0, 1)) = 0.8
        _Outline2Width ("Width (m)", Range(0.0002, 0.006)) = 0.0012
        _Shape2Scale ("Size vs Main Blob", Range(0.5, 1.5)) = 1.1
        _Shape2Offset ("Offset (m)", Vector) = (0.004, 0.006, -0.003, 0)
        _Shape2Seed ("Lump Seed", Float) = 37

        [Header(Sparkles)]
        _SparkleColor ("Sparkle Color (hue from _Hue)", Color) = (1, 0.97, 1, 1)
        _SparkleDensity ("Sparkle Density", Range(0, 1)) = 0.2
        _SparkleCell ("Sparkle Spacing (m)", Range(0.003, 0.05)) = 0.011
        _SparkleSize ("Sparkle Size (m)", Range(0.0002, 0.005)) = 0.0011
        _SparkleSpeed ("Twinkle Speed", Range(0, 10)) = 2.5
        _SparkleDrift ("Drift Speed (m/s)", Range(0, 0.05)) = 0

        [Header(Bounds)]
        _BoxMin ("Box Min", Vector) = (-0.06, -0.06, -0.06, 0)
        _BoxMax ("Box Max", Vector) = (0.06, 0.125, 0.06, 0)
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "VRCFallback"="Hidden" }

        Pass
        {
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            // Back faces, so it still renders with the camera inside the box.
            Cull Front

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 4.5
            #include "UnityCG.cginc"

            float _Glow, _Hue;
            float4 _Color, _EdgeColor, _SparkleColor;
            float _CenterAlpha, _EdgeAlpha, _EdgePower, _Feather;
            float4 _PillA, _PillB, _BoxMin, _BoxMax;
            float _PillRadius, _PillTipRadius, _PillFlare, _GrowFrom, _LumpAmount, _LumpScale, _LumpSpeed;
            float _Outline2Alpha, _Outline2Width, _Shape2Scale, _Shape2Seed;
            float4 _Shape2Offset;
            float _SparkleDensity, _SparkleCell, _SparkleSize, _SparkleSpeed, _SparkleDrift;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 opos : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = _Glow < 0.001 ? float4(0, 0, 0, 0) : UnityObjectToClipPos(v.vertex);
                o.opos = v.vertex.xyz;
                return o;
            }

            float hash31(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.zyx + 31.32);
                return frac((p.x + p.y) * p.z);
            }

            float3 hash33(float3 p)
            {
                p = frac(p * float3(0.1031, 0.1030, 0.0973));
                p += dot(p, p.yxz + 33.33);
                return frac((p.xxy + p.yxx) * p.zyx);
            }

            // PCG3D integer hash: well spread for neighbouring sparkle cells, unlike frac hashes.
            float3 rand3(float3 cell)
            {
                uint3 v = asuint(int3(cell)) * 1664525u + 1013904223u;
                v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
                v ^= v >> 16u;
                v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
                return float3(v) / 4294967295.0;
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

            // Pill radius along its axis (h 0 at the base, 1 at the tip), flaring out toward the base.
            float PillRadius(float h) { return lerp(_PillTipRadius, _PillRadius, pow(1 - h, _PillFlare)); }

            // Signed distance to the aura, in metres (object space).
            float map(float3 p)
            {
                float s = lerp(_GrowFrom, 1, smoothstep(0, 1, _Glow));
                float3 pa = p - _PillA.xyz, ba = _PillB.xyz - _PillA.xyz;
                float h = saturate(dot(pa, ba) / max(dot(ba, ba), 1e-8));
                // Tapered: an approximate distance; the marcher's 0.8 step factor covers the error.
                float d = length(pa - ba * h) - PillRadius(h) * s;
                // Slowly churning lumps, two octaves.
                float3 q = p * _LumpScale + float3(0, _Time.y * _LumpSpeed, _Time.y * _LumpSpeed * 0.6);
                float n = valueNoise(q) * 0.65 + valueNoise(q * 2.1 + 5.3) * 0.35;
                return d + (n - 0.5) * 2 * _LumpAmount * s;
            }

            // The second blob: same tapered pill, scaled and offset, with its own single-octave
            // lumps. Only its outline is drawn.
            float map2(float3 p)
            {
                float s = lerp(_GrowFrom, 1, smoothstep(0, 1, _Glow)) * _Shape2Scale;
                p -= _Shape2Offset.xyz;
                float3 pa = p - _PillA.xyz, ba = _PillB.xyz - _PillA.xyz;
                float h = saturate(dot(pa, ba) / max(dot(ba, ba), 1e-8));
                float d = length(pa - ba * h) - PillRadius(h) * s;
                float3 q = p * _LumpScale * 0.9 + _Shape2Seed + float3(_Time.y * _LumpSpeed * 0.7, 0, -_Time.y * _LumpSpeed);
                return d + (valueNoise(q) - 0.5) * 2.4 * _LumpAmount * s;
            }

            // How close the ray comes to the second blob's edge. Rays that pass just outside
            // it or graze just inside it form its outline; rays that go deep inside don't.
            // Returns (outline strength, distance along the ray to draw it at).
            float2 outline2(float3 ro, float3 rd, float2 box)
            {
                float w = _Outline2Width;
                float t = max(box.x, 0), best = 1e5, tBest = t;
                [loop] for (int k = 0; k < 40 && t < box.y; k++)
                {
                    float d = map2(ro + rd * t);
                    if (d < -w) return float2(0, t);
                    if (abs(d) < abs(best)) { best = d; tBest = t; }
                    t += max(abs(d) * 0.8, w * 0.5);
                }
                return float2(1 - smoothstep(0, w, abs(best)), tBest);
            }

            float3 mapNormal(float3 p)
            {
                const float e = 0.0005;
                return normalize(float3(
                    map(p + float3(e, 0, 0)) - map(p - float3(e, 0, 0)),
                    map(p + float3(0, e, 0)) - map(p - float3(0, e, 0)),
                    map(p + float3(0, 0, e)) - map(p - float3(0, 0, e))));
            }

            float2 rayBox(float3 ro, float3 rd)
            {
                float3 inv = 1 / rd;
                float3 t0 = (_BoxMin.xyz - ro) * inv, t1 = (_BoxMax.xyz - ro) * inv;
                float3 lo = min(t0, t1), hi = max(t0, t1);
                return float2(max(max(lo.x, lo.y), lo.z), min(min(hi.x, hi.y), hi.z));
            }

            // The sparkle in the cell containing p, if it has one and it is inside the aura:
            // four sharp tapering rays in the camera's plane, measured from the ray. No round core.
            float sparkle(float3 p, float3 ro, float3 rd, float3 right, float3 up)
            {
                float3 drift = float3(0, _Time.y * _SparkleDrift, 0);
                float3 cell = floor((p - drift) / _SparkleCell);
                float3 h = rand3(cell);
                if (h.x > _SparkleDensity) return 0;
                float3 star = (cell + 0.15 + 0.7 * rand3(cell + 101)) * _SparkleCell + drift;
                if (map(star) > -_SparkleSize) return 0;
                float3 w = star - ro;
                float3 perp = w - rd * dot(w, rd);
                float x = abs(dot(perp, right)), y = abs(dot(perp, up));
                float s = _SparkleSize * (0.6 + 0.8 * h.y);
                // The brighter of the two rays, not their sum, so the centre isn't a hot spot.
                float rays = max(exp(-x / (s * 0.15) - y / (s * 1.6)), exp(-y / (s * 0.15) - x / (s * 1.6)));
                // Mostly resting dim, with a brief flash each cycle.
                float twinkle = pow(0.5 + 0.5 * sin(_Time.y * _SparkleSpeed * (0.6 + h.z) + h.y * 6.2832), 8);
                return rays * 0.8 * twinkle;
            }

            // The colour with its hue replaced; saturation and brightness are kept.
            float3 WithHue(float3 c, float hue)
            {
                float hi = max(c.r, max(c.g, c.b)), lo = min(c.r, min(c.g, c.b));
                float3 pure = saturate(abs(frac(hue + float3(1, 2.0 / 3, 1.0 / 3)) * 6 - 3) - 1);
                return lo + (hi - lo) * pure;
            }

            float4 frag(v2f i, out float depth : SV_Depth) : SV_Target
            {
                // The ray march, sparkle axes and output depth use the object matrices and this
                // eye's camera position, view and projection (single-pass instanced stereo).
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 magic = WithHue(_Color.rgb, _Hue), edgeColor = WithHue(_EdgeColor.rgb, _Hue);
                float3 ro = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1)).xyz;
                float3 rd = normalize(i.opos - ro);
                float2 box = rayBox(ro, rd);
                float t = max(box.x, 0);
                float minD = 1e5, tMin = t;
                bool hit = false;
                [loop] for (int k = 0; k < 64 && t < box.y; k++)
                {
                    float d = map(ro + rd * t);
                    if (d < minD) { minD = d; tMin = t; }
                    if (d < 0.0002) { hit = true; break; }
                    // Lumps make the field inexact; step a bit short.
                    t += max(d * 0.8, 0.0003);
                }

                float alpha;
                float3 col;
                float3 surface;
                float spark = 0;
                if (hit)
                {
                    surface = ro + rd * t;
                    float3 n = mapNormal(surface);
                    float edge = pow(1 - saturate(dot(n, -rd)), _EdgePower);
                    alpha = lerp(_CenterAlpha, _EdgeAlpha, edge);
                    col = lerp(magic, edgeColor, edge);

                    // Through the inside for sparkles.
                    float3 right = normalize(mul((float3x3)unity_WorldToObject, UNITY_MATRIX_V[0].xyz));
                    float3 up = normalize(mul((float3x3)unity_WorldToObject, UNITY_MATRIX_V[1].xyz));
                    float stepLen = _SparkleCell * 0.5;
                    float ts = t + stepLen * 0.5;
                    [loop] for (int j = 0; j < 24 && ts < box.y; j++)
                    {
                        float3 p = ro + rd * ts;
                        if (map(p) > 0.002) break;
                        spark = max(spark, sparkle(p, ro, rd, right, up));
                        ts += stepLen;
                    }
                }
                else
                {
                    // Just outside: a short soft rim.
                    surface = ro + rd * tMin;
                    alpha = _EdgeAlpha * (1 - smoothstep(0, _Feather, minD));
                    col = edgeColor;
                }

                // Second outline, seen through the main blob (which is in front of it).
                float2 o2 = outline2(ro, rd, box);
                float a2 = o2.x * _Outline2Alpha;
                if (!hit && a2 > alpha) surface = ro + rd * o2.y;
                col = col * alpha + edgeColor * a2 * (1 - alpha);
                alpha = alpha + a2 * (1 - alpha);
                col /= max(alpha, 1e-4);
                if (alpha < 0.002) discard;

                float glow = saturate(_Glow * 1.5);
                alpha *= glow;
                spark *= glow;
                float4 clip = UnityObjectToClipPos(float4(surface, 1));
                depth = clip.z / clip.w;
                #if !defined(UNITY_REVERSED_Z)
                depth = depth * 0.5 + 0.5;
                #endif
                return float4(col * alpha + WithHue(_SparkleColor.rgb, _Hue) * spark, saturate(alpha + spark));
            }
            ENDCG
        }
    }
    FallBack Off
}
