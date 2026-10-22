// Neon glow for Tabletop Simulator (built-in pipeline, goes into the AssetBundle with the material). Works on ANY mesh:
// tubes from the Neon Text window (Editor\NeonTextWindow.cs), tubes from Blender, game models, a cube.
// Unlit, four passes in one material: the tube, then three for the halo.
// - Tube (opaque): glowing gas, not a shiny surface. The light is the thickness of gas along the view (N.V on a round
//   tube: brightest in the middle), turned into colour like an exposed photo: 1 - exp(-colour x light x exposure), so
//   bright parts go white smoothly while the edges stay saturated. No highlight (a white stripe looked like plastic).
//   A dark tube (off, broken letter) is matte coated glass, not black: light grey with a hint of the colour, lit like
//   the other objects (ambient ShadeSH9 + the main directional light, soft wrap), with a thin Fresnel reflection of the
//   environment (unity_SpecCube0: the sky or a reflection probe) at the outline.
//   _MainTex is an optional glow mask (white = glows; e.g. a game model's emissive map).
// - Halo (additive): the mesh blown up by _HaloSize (mesh units, so it scales with the object, also in TTS), faded
//   towards its outline by N.V. Where shells overlap (joints of tubes, neighbouring letters) the strongest one counts,
//   not the sum, which made bright balls at every joint: max of the shape into the frame's alpha, then colour x alpha
//   added once per pixel (stencil bit 128). The first halo pass clears both alpha and that bit under the shell, so no
//   pass is needed afterwards to clean up for the next object; the bit stays set after the last neon object of the
//   frame (only another shader using stencil bit 128 would notice). Direction: along the normals (smooth meshes: tubes, spheres) or from the
//   object centre (cubes and other hard-edged shapes: the shell stays closed and fades like a ball).
//   The Halo checkbox (Editor\NeonHaloDrawer.cs) switches the three halo passes off in the material itself
//   (Material.SetShaderPassEnabled on their LightMode "Always"; saved in the .mat, so also in the AssetBundle): a sign
//   without halo is one draw call instead of four. Halo strength 0 does not do that, the passes would still be drawn.
// Flicker, all with 0 = off: hum, broken letters, whole-object glitches, power-on cycle, chase, second colour.
// It depends only on the letter and the time, so it is worked out per vertex: in the pixel shader the interpolated
// letter seed differs by rounding from pixel to pixel, and the hash of hours of _Time turns that into noise.
// Per-letter data is in uv4 (x = position along the sign 0..1, y = 1 broken letter, z = 1 "letters marked", w = 1 "uv3
// holds smooth halo normals" for flat-faced tubes; the Neon Text window writes it). Not uv2: most meshes keep lightmap UVs there (Unity's cube and sphere do). A mesh without
// the mark flickers as one piece. Each object gets its own time offset from its world position, so copies do not
// flicker in sync (DisableBatching keeps the object position for that).
// Queue Transparent-10: drawn after the skybox (else the sky would paint over the additive halo).
// Gamma editor vs Linear TTS: told at run time by _CSProbe = 0.5 grey; everything is worked out
// in display (gamma) values and converted at the end, so it looks the same in both.
// _Core and _CoreWidth keep their first names (shown as exposure and edge falloff): renaming a property would make
// existing materials drop their values silently.
Shader "Custom/Neon"
{
    Properties
    {
        _Color ("Colour (TTS tint)", Color) = (1, 0.12, 0.3, 1)
        _MainTex ("Glow mask (optional, white = glows)", 2D) = "white" {}
        [Header(Tube)]
        _Brightness ("Brightness", Range(0, 10)) = 1.5
        _Core ("White-hot core (exposure)", Range(0, 3)) = 0.7
        _CoreWidth ("Edge falloff", Range(0, 1)) = 0.5
        _Glass ("Unlit glass brightness (x scene light)", Range(0, 5)) = 1
        [Header(Halo)]
        [NeonHalo] _Halo ("Halo (off = the 3 halo passes are not drawn at all)", Float) = 1
        _HaloSize ("Halo size (mesh units)", Range(0, 5)) = 0.07
        _HaloStrength ("Halo strength", Range(0, 5)) = 0.6
        [Enum(Along normals, 0, From object centre, 1)] _HaloDir ("Halo direction", Float) = 0
        [Header(Flicker)]
        _Hum ("Hum: shimmer depth", Range(0, 1)) = 0.05
        _BrokenRate ("Broken letters: switches per second", Range(0.05, 30)) = 1.2
        _BrokenOff ("Broken letters: share of time dark", Range(0, 1)) = 0.4
        _BrokenStutter ("Broken letters: stutter when switching", Range(0, 1)) = 0.6
        _Glitch ("Whole object: glitches per minute", Range(0, 240)) = 1
        [Header(Power on cycle)]
        _SeqPeriod ("Cycle length, s (0 = off)", Range(0, 120)) = 0
        _SeqLight ("Share of the cycle to light all letters", Range(0.01, 0.95)) = 0.3
        [Header(Chase)]
        _Chase ("Strength (0 = off)", Range(0, 1)) = 0
        _ChaseSpeed ("Speed, sign lengths per second", Range(-10, 10)) = 0.5
        _ChaseWidth ("Width, share of the sign", Range(0.01, 2)) = 0.25
        [Header(Second colour)]
        _Color2 ("Second colour", Color) = (0.1, 0.85, 1, 1)
        _ColorMix ("Amount (0 = off)", Range(0, 1)) = 0
        _ColorSpeed ("Cycle, Hz", Range(0, 10)) = 0
        _ColorWave ("Waves along the sign", Range(0, 10)) = 0
        [HideInInspector] _CSProbe ("Colour space probe (keep 0.5 grey)", Color) = (0.5, 0.5, 0.5, 1)
    }

    SubShader
    {
        Tags { "Queue" = "Transparent-10" "RenderType" = "Opaque" "IgnoreProjector" = "True" "DisableBatching" = "True" }

        CGINCLUDE
        #include "UnityCG.cginc"
        #include "Lighting.cginc"

        sampler2D _MainTex;
        float4 _MainTex_ST;
        fixed4 _Color, _Color2, _CSProbe;
        float _Brightness, _Core, _CoreWidth, _Glass, _HaloSize, _HaloStrength, _HaloDir;
        float _Hum, _BrokenRate, _BrokenOff, _BrokenStutter, _Glitch, _SeqPeriod, _SeqLight;
        float _Chase, _ChaseSpeed, _ChaseWidth, _ColorMix, _ColorSpeed, _ColorWave;

        struct appdata
        {
            float4 vertex : POSITION;
            float3 normal : NORMAL;
            float2 uv : TEXCOORD0;
            float3 haloNormal : TEXCOORD2;   // uv3: smooth normals for the halo of flat-faced tubes (when uv4.w = 1)
            float4 letter : TEXCOORD3;   // uv4: x = position along the sign 0..1, y = 1 broken letter, z = 1 marked
        };

        struct v2f
        {
            float4 pos : SV_POSITION;
            float2 uv : TEXCOORD0;
            float4 glow : TEXCOORD1;     // rgb = tint (display values), a = power 0..1.75
            float3 wn : TEXCOORD2;
            float3 wp : TEXCOORD3;
            UNITY_FOG_COORDS(4)
        };

        // ---- colour space: the engine hands colour properties over linearised in a Linear player only

        bool LinearSpace() { return _CSProbe.r < 0.35; }
        float3 ToDisplay(float3 c) { return LinearSpace() ? pow(max(c, 0.0), 1.0 / 2.2) : c; }   // engine value -> display
        float3 ToOutput(float3 c) { return LinearSpace() ? pow(max(c, 0.0), 2.2) : c; }          // display -> frame
        float ShapeToOutput(float h) { return LinearSpace() ? pow(h, 2.2) : h; }

        // ---- flicker

        float hash11(float p)
        {
            p = frac(p * 0.1031);
            p *= p + 33.33;
            p *= p + p;
            return frac(p);
        }

        float noise1(float x)
        {
            float i = floor(x);
            float f = frac(x);
            return lerp(hash11(i), hash11(i + 1), f * f * (3 - 2 * f));
        }

        // 0..1 (up to 1.75 under the chase wave): how much this letter is lit at time t
        float Power(float pos, float broken, float seed, float objSeed, float t)
        {
            float on = 1 - _Hum * noise1(t * 13 + seed * 7.3);

            if (broken > 0.5)
            {
                float x = t * _BrokenRate + seed * 3.1;
                float lit = step(_BrokenOff, hash11(floor(x) + seed * 17));
                float fast = step(0.45, hash11(floor(t * 23) + seed * 5));
                on *= lerp(lit, fast, step(frac(x), 0.35 * _BrokenStutter));
            }

            // about _Glitch per minute: a quarter second in which the whole object stutters
            float g = hash11(floor(t * 4) * 1.37 + objSeed) < _Glitch / 240.0 ? 1 : 0;
            on *= lerp(1, 0.1 + 0.9 * step(0.5, hash11(floor(t * 20) + objSeed)), g);

            if (_SeqPeriod > 0)
            {
                float p = frac(t / _SeqPeriod);
                float tail = min(0.8 / _SeqPeriod, 0.15);            // all dark at the end of the cycle
                float share = min(_SeqLight, 0.95 - tail);           // the last letter must get its turn before that
                float dt = (p - pos * share) * _SeqPeriod;           // seconds since this letter's turn
                float lit = step(0, dt) * step(p, 1 - tail);
                float crackle = step(dt, 0.4);                       // the first 0.4 s it tries to light
                on *= lit * lerp(1, step(0.4, hash11(floor(t * 25) + seed * 11)), crackle);
            }

            float w = _ChaseWidth;
            float ph = frac(t * _ChaseSpeed) * (1 + 2 * w) - w;
            on *= lerp(1, 0.25 + 1.5 * smoothstep(w, 0, abs(pos - ph)), _Chase);
            return on;
        }

        // ---- vertex

        v2f vertCommon(appdata v, float3 objPos, float3 objNormal)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(float4(objPos, 1));
            o.uv = TRANSFORM_TEX(v.uv, _MainTex);
            o.wn = UnityObjectToWorldNormal(objNormal);
            o.wp = mul(unity_ObjectToWorld, float4(objPos, 1)).xyz;

            float3 p = unity_ObjectToWorld._m03_m13_m23;
            float objSeed = frac(sin(dot(p, float3(12.9898, 78.233, 37.719))) * 43758.5453) * 100 + 7.31;   // never 0: hash11(0) = 0
            float2 l = v.letter.z > 0.5 ? v.letter.xy : float2(0, 0);
            float seed = frac(l.x * 97.13 + 0.371) * 50 + objSeed;
            float t = _Time.y + objSeed;
            float colourWave = 0.5 + 0.5 * cos(6.2832 * (t * _ColorSpeed - l.x * _ColorWave));
            float3 tint = lerp(ToDisplay(_Color.rgb), ToDisplay(_Color2.rgb), _ColorMix * colourWave);
            o.glow = float4(tint, Power(l.x, l.y, seed, objSeed, t));
            UNITY_TRANSFER_FOG(o, o.pos);
            return o;
        }

        // the mesh blown up by _HaloSize along the normals (the smooth halo normals of a flat-faced tube from the Neon
        // Text window: its split normals would tear the shell open at every edge) or from the object centre
        v2f vertHalo(appdata v)
        {
            float3 n = v.letter.w > 0.5 ? v.haloNormal : v.normal;
            float3 dir = _HaloDir < 0.5 ? n : v.vertex.xyz;
            dir = normalize(dir + float3(0, 0, 1e-6));   // a vertex right at the centre would give NaN
            return vertCommon(v, v.vertex.xyz + dir * _HaloSize, dir);
        }

        // ---- pixel

        // tint (display values, x glow mask) and power of this pixel
        void Glow(v2f i, out float3 tint, out float on)
        {
            fixed4 m = tex2D(_MainTex, i.uv);
            tint = i.glow.rgb * max(m.r, max(m.g, m.b));
            on = i.glow.a;
        }

        float FacingCamera(v2f i)
        {
            return saturate(dot(normalize(i.wn), normalize(_WorldSpaceCameraPos - i.wp)));
        }
        ENDCG

        Pass
        {
            Name "TUBE"
            Tags { "LightMode" = "ForwardBase" }   // so the scene light (SH, main light) is set for the dark glass
            ZWrite On
            Cull Back

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma target 3.0

            v2f vert(appdata v) { return vertCommon(v, v.vertex.xyz, v.normal); }

            // matte coated glass of a dark tube + Fresnel reflection of the environment (display values)
            float3 DarkGlass(v2f i, float3 tint, float nv)
            {
                float3 n = normalize(i.wn);
                float3 L = _WorldSpaceLightPos0.xyz;   // main directional light, 0 if none
                float3 V = normalize(_WorldSpaceCameraPos - i.wp);
                float wrap = dot(n, L) * 0.5 + 0.5;
                float3 diff = ToDisplay(max(ShadeSH9(float4(n, 1)), 0.0) + _LightColor0.rgb * (wrap * wrap));
                float3 env = ToDisplay(DecodeHDR(UNITY_SAMPLE_TEXCUBE(unity_SpecCube0, reflect(-V, n)), unity_SpecCube0_HDR));
                float rim = 1 - nv;
                float fresnel = 0.04 + 0.96 * rim * rim * rim * rim * rim;
                float3 body = lerp(float3(0.85, 0.85, 0.87), tint, 0.15) * diff * 0.75;
                return lerp(body, env, fresnel) * _Glass;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 tint;
                float on;
                Glow(i, tint, on);
                float nv = FacingCamera(i);
                float gas = _Brightness * on * lerp(1 - _CoreWidth, 1, nv * nv);   // thickness of gas along the view
                float3 lit = 1 - exp(-tint * gas * lerp(1, 10, _Core));           // exposed: bright goes white
                float3 col = ToOutput(lit + DarkGlass(i, tint, nv) * (1 - saturate(on)));
                UNITY_APPLY_FOG(i.fogCoord, col);
                return fixed4(col, 1);
            }
            ENDCG
        }

        // Halo 1/3: clear the frame's alpha and stencil bit 128 under the shell
        Pass
        {
            Name "HALO_CLEAR"
            Tags { "LightMode" = "Always" }   // the Halo checkbox switches passes off by this tag
            ZWrite Off
            ColorMask A
            Blend Zero Zero
            Cull Back
            Stencil { Ref 0 ReadMask 128 WriteMask 128 Comp Always Pass Replace }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            v2f vert(appdata v) { return vertHalo(v); }
            fixed4 frag(v2f i) : SV_Target { return 0; }
            ENDCG
        }

        // Halo 2/3: the strongest halo shape of all overlapping shells into alpha
        Pass
        {
            Name "HALO_SHAPE"
            Tags { "LightMode" = "Always" }   // the Halo checkbox switches passes off by this tag
            ZWrite Off
            ColorMask A
            BlendOp Max
            Blend One One
            Cull Back

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            v2f vert(appdata v) { return vertHalo(v); }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 tint;
                float on;
                Glow(i, tint, on);
                float f = FacingCamera(i);
                float shape = _HaloDir < 0.5 ? f * f : f;   // a shell blown from the centre sticks out less at the outline
                return fixed4(0, 0, 0, ShapeToOutput(saturate(shape * on)));
            }
            ENDCG
        }

        // Halo 3/3: colour x that alpha, once per pixel (sets stencil bit 128)
        Pass
        {
            Name "HALO_COLOUR"
            Tags { "LightMode" = "Always" }   // the Halo checkbox switches passes off by this tag
            ZWrite Off
            ColorMask RGB
            Blend DstAlpha One
            Cull Back
            Stencil { Ref 128 ReadMask 128 WriteMask 128 Comp NotEqual Pass Replace }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma target 3.0
            v2f vert(appdata v) { return vertHalo(v); }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 tint;
                float on;
                Glow(i, tint, on);
                float3 col = ToOutput(tint * _HaloStrength);
                UNITY_APPLY_FOG_COLOR(i.fogCoord, col, fixed3(0, 0, 0));
                return fixed4(col, 1);
            }
            ENDCG
        }
    }
}
