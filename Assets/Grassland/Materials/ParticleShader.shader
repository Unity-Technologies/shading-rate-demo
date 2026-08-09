Shader "Unlit/ParticleShader"
{
    // Draws GPU-indirect grass clumps. Geometry (the baked blade clump) and per-instance clump
    // positions come from StructuredBuffers bound per-draw via a MaterialPropertyBlock, so one
    // material serves all LODs. Terrain conform is baked into culledPositionsBuffer.y by the
    // compute pass, so this shader does no heightmap displacement.
    Properties
    {
        _Colormap("Color map", 2D) = "white" {}
        _Color0("Base tint", Color) = (0.0, 0.0, 0.0, 1.0)
        _Color1("Tip tint", Color) = (1.0, 1.0, 1.0, 1.0)
        [NoScaleOffset] _LayerTex("LOD texture (RGB = color, A = coverage)", 2D) = "white" {}
        _AlphaCutoff("Alpha cutoff", Range(0, 1)) = 0.4

        [Header(Lighting)]
        _SubsurfaceColor("Subsurface color", Color) = (0.45, 0.72, 0.18, 1.0)
        _SssIntensity("Subsurface intensity", Range(0, 4)) = 1.3
        _SssPower("Subsurface power", Range(1, 16)) = 4.0
        _SssBase("Subsurface base (front-lit glow)", Range(0, 1)) = 0.2
        _AmbientStrength("Ambient strength", Range(0, 2)) = 1.2
        _DeformDarken("Deformed grass darkness", Range(0, 1)) = 0.55
        _BaseOcclusion("Base occlusion (fake AO at the base)", Range(0, 1)) = 0.55
        _BaseOcclusionReach("Base occlusion reach (up the model)", Range(0.02, 1)) = 0.35
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        LOD 100
        Cull Off

        Pass
        {
            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct vertdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                uint vid : SV_VertexID;
                uint iid : SV_InstanceID;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                float height : TEXCOORD1;
                float3 worldPos : TEXCOORD2;
                float deform : TEXCOORD3;
                float fog : TEXCOORD4;
                float2 lodFade : TEXCOORD5; // x = band position t (0..1), y = role (0 fade-out, 1 fade-in)
            };

            struct fragdata
            {
                float4 col : SV_Target;
            };

            TEXTURE2D(_Colormap);
            SAMPLER(sampler_Colormap);
            TEXTURE2D(_LayerTex);   // this draw's RGBA texture: RGB = albedo, A = coverage (MPB)
            SAMPLER(sampler_LayerTex);
            float _AlphaCutoff;     // clip threshold on the coverage, MPB

            CBUFFER_START(UnityPerMaterial)
                float4 _Colormap_ST;
                half4 _Color0;
                half4 _Color1;
                half4 _SubsurfaceColor;
                float _SssIntensity;
                float _SssPower;
                float _SssBase;
                float _AmbientStrength;
                float _DeformDarken;
                float _BaseOcclusion;
                float _BaseOcclusionReach;
            CBUFFER_END

            // Bound per-LOD via MaterialPropertyBlock.
            StructuredBuffer<float3> vertexBuffer;
            StructuredBuffer<float2> uvBuffer;
            StructuredBuffer<float4> culledPositionsBuffer;

            // Shared per-frame uniforms (set on the material).
            float modelHeight;
            float worldScale;
            float time;
            float4 wind; // xyz = direction, w = strength
            float4 clumpScale; // per-clump geometry scale (xyz); fills gaps between clumps
            sampler2D _DeformTex; // persistent trail texture (r = flatten amount over the field)
            float2 fieldOrigin;   // world XZ center of the field (for deform-texture lookup)
            float deformFlatten;  // how strongly the trail flattens grass (per-draw, MPB)
            float deformCull;     // >0: this layer shrinks away instead of flattening (per-draw, MPB)

            // LOD screen-door cross-fade (matches the band routing in ComputeCulling). Two LODs per
            // layer, one boundary: in the band the clump is drawn at both LODs and dithered
            // complementarily. lodBandWidth == 0 => hard switch. lodLevel + windScale are per-draw (MPB).
            float lodLevel;      // 0 = near LOD, 1 = far LOD (this draw)
            float3 lodCenter;    // camera world pos (LOD distance is measured from here)
            float lodDistance;   // near->far boundary radius (m) for this layer
            float lodBandWidth;  // transition band width (m); 0 = hard
            float windScale;     // per-layer wind sway multiplier
            float heightVariance;// per-layer random per-clump height variation (fraction, 0..1)
            float _DebugLod;     // 1 = tint by LOD (green = near/LOD0, red = far/LOD1)

            // Whether this draw has an RGBA texture bound. Uniform per draw, so the branch in frag()
            // is uniform and texture derivatives stay valid.
            float textured;      // 1 = a texture is bound for this draw, 0 = procedural only (MPB)
            float coverageOnly;  // 1 = that texture is a MASK: coverage from it, color still from the
                                 // gradient, so an impostor matches the geometry it stands in for (MPB)
            float4 coverageChannel; // which channels carry the coverage; resolved from the texture's
                                    // own format on the CPU, so grayscale and alpha masks both work

            // "Look at": face a fixed world direction instead of a free random yaw. Resolved from the
            // layer's direction on the CPU, so this is just the angles. All per-draw (MPB).
            float lookAtEnabled;
            float lookAtYaw;      // radians, from the direction's XZ azimuth
            float lookAtPitch;    // radians, from its elevation, already damped by the layer's tilt
            float lookAtVariance; // radians of random per-instance spread around both

            // Stable per-clump random in [0,1) from a world XZ position (used for height variance).
            // Keyed to world position (not the buffer index, which culling reorders) so it doesn't
            // flicker frame to frame.
            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            // 4x4 ordered (Bayer) dither threshold in [0,1) from screen-pixel position.
            static const float _Bayer4x4[16] = {
                 0.0/16.0,  8.0/16.0,  2.0/16.0, 10.0/16.0,
                12.0/16.0,  4.0/16.0, 14.0/16.0,  6.0/16.0,
                 3.0/16.0, 11.0/16.0,  1.0/16.0,  9.0/16.0,
                15.0/16.0,  7.0/16.0, 13.0/16.0,  5.0/16.0
            };
            // The procedural look: the colormap sampled down its height at mid-width, plus the base/tip
            // tints. Shared by the untextured draws and by coverage-only impostors, which is the whole
            // point — both LODs run the same expression, so retuning the tints moves them together.
            float3 ColormapAlbedo(float height)
            {
                float4 colmap = SAMPLE_TEXTURE2D(_Colormap, sampler_Colormap, float2(0.5, height));
                float a = _Color0.w * (1.0 - height) + _Color1.w * height;
                return colmap.xyz + lerp(_Color0.xyz * _Color0.w, _Color1.xyz * _Color1.w, height * a);
            }

            float LodDitherThreshold(float2 pixel)
            {
                int2 p = int2(fmod(pixel, 4.0));
                return _Bayer4x4[p.y * 4 + p.x] + (0.5 / 16.0);
            }

            v2f vert(vertdata v)
            {
                v2f o;
                o.uv = uvBuffer[v.vid];

                float4 inst = culledPositionsBuffer[v.iid]; // xyz = world pos (conformed), w = packed yaw+size
                float3 vRaw = vertexBuffer[v.vid];
                float bladeH = saturate(vRaw.y / max(modelHeight, 1e-4));

                // Unpack per-instance size + yaw from .w (integer part = size*1023, fraction = yaw/2pi).
                // Size tapers clumps down near the mask-out mesh (road) so grass blends into the edge.
                float instScale = floor(inst.w) / 1023.0;
                float rnd = frac(inst.w);            // per-instance random, and the free yaw itself
                float yaw = rnd * 6.2831853;
                float pitch = 0.0;

                // Look-at layers replace that free yaw with the target direction plus a per-instance
                // spread — a field of sunflowers all leaning one way without being clones. The spread
                // uses a second hash for pitch so it doesn't correlate with the yaw offset.
                if (lookAtEnabled > 0.5)
                {
                    yaw   = lookAtYaw   + (rnd - 0.5) * 2.0 * lookAtVariance;
                    pitch = lookAtPitch + (Hash21(inst.xz * 1.37 + 7.3) - 0.5) * 2.0 * lookAtVariance;
                }

                // Per-clump scale (layer clumpScale x per-instance size), then orient, then place.
                float3 vp = vRaw * clumpScale.xyz * instScale;
                // Per-clump random height variation (stable hash of world XZ), height axis only.
                vp.y *= 1.0 + (Hash21(inst.xz) - 0.5) * 2.0 * heightVariance;
                // Pitch about local X first — tilts the +Z facing axis up toward the target — then yaw
                // about Y. pitch is 0 for every non-look-at layer, so this is an identity for them.
                float sp = sin(pitch), cp = cos(pitch);
                vp.yz = float2(cp * vp.y + sp * vp.z, -sp * vp.y + cp * vp.z);
                float sa = sin(yaw), ca = cos(yaw);
                vp.xz = float2(ca * vp.x + sa * vp.z, -sa * vp.x + ca * vp.z);
                float3 instanceOffset = inst.xyz;
                float3 worldPos = vp + instanceOffset;

                // Blade-height-weighted wind sway (tips move, base stays planted).
                float x = worldPos.x / (worldScale / 2.0f) * 0.5f + 0.5f;
                float y = worldPos.z / (worldScale / 2.0f) * 0.5f + 0.5f;
                float3 dirWind = normalize(float3(wind.x, wind.y, wind.z) + 1e-5);
                float t = time * 0.5;
                // Height-weighted wind sway (base stays planted, top sways). Per-layer windScale can
                // be set to 0 for stiff impostors that shouldn't move.
                {
                    float3 windOffset = dirWind * wind.w * 0.25 * (sin(t * 3.14) * 0.5 + 0.5);
                    windOffset += float3(dirWind.x, -0.5, dirWind.z) * wind.w * 0.5
                        * smoothstep(0.0, 1.0, max(0.0, cos(-x * 2.0 - t * 0.5) * cos(-y * 2.0 - t * 1.2)));
                    worldPos += windOffset * bladeH * windScale * instScale;
                }

                // Deformation trail: sample the persistent flatten texture at this blade's world XZ
                // and press the blade toward the ground (base stays planted). The trail regrows as
                // the texture decays over time.
                float2 duv = saturate((worldPos.xz - fieldOrigin) / worldScale + 0.5);
                float deform = tex2Dlod(_DeformTex, float4(duv, 0, 0)).r;
                if (deformCull > 0.0)
                {
                    // Remove layers shrink toward their base instead of folding over — a rigid model
                    // like a flower reads as broken when flattened. The compute pass culls them once
                    // the trail passes deformCull, so this ramp reaches 0 exactly where that cut is:
                    // they scale away and grow back rather than popping in and out.
                    float k = saturate(1.0 - deform / deformCull);
                    worldPos = instanceOffset + (worldPos - instanceOffset) * k;
                }
                else
                {
                    worldPos.y -= vp.y * saturate(deform * deformFlatten);
                }

                o.vertex = TransformWorldToHClip(worldPos);
                o.height = bladeH;
                o.worldPos = worldPos;
                o.deform = deform;
                o.fog = ComputeFogFactor(o.vertex.z);

                // LOD cross-fade band position for this clump (mirrors ComputeCulling's single-
                // boundary band routing). Uses the clump's world XZ (inst.xz) so it's constant across
                // the clump. Outside the band, default (0,0) = fade-out role with t=0 => keep every
                // pixel, which is correct for both the pure-near and pure-far regions.
                float2 lodFade = float2(0.0, 0.0);
                if (lodBandWidth > 1e-4)
                {
                    float d = distance(inst.xz, lodCenter.xz);
                    float hb = lodBandWidth * 0.5;
                    if (d > lodDistance - hb && d < lodDistance + hb)
                    {
                        float t = saturate((d - (lodDistance - hb)) / lodBandWidth);
                        // near LOD (lodLevel 0) fades OUT as d grows; far LOD (lodLevel 1) fades IN.
                        lodFade = float2(t, lodLevel < 0.5 ? 0.0 : 1.0);
                    }
                }
                o.lodFade = lodFade;
                return o;
            }

            fragdata frag(v2f i)
            {
                fragdata fragout;

                // LOD screen-door cross-fade: complementary ordered-dither between the two LODs
                // drawn in a transition band. role 0 (fade-out): keep where dither >= t; role 1
                // (fade-in): keep where dither < t. Together the pair partitions the pixels exactly,
                // so coverage stays constant across the boundary. Skipped when lodBandWidth == 0.
                if (lodBandWidth > 1e-4)
                {
                    float thr = LodDitherThreshold(i.vertex.xy);
                    float t = i.lodFade.x;
                    clip(i.lodFade.y < 0.5 ? (thr - t) : (t - thr));
                }

                // Coverage + albedo, branched on the per-draw `textured` uniform (constant across the
                // draw, so the branch is uniform and derivatives/mips stay valid):
                //
                //   textured == 1  this LOD has an RGBA texture — a flower's base color, or an
                //                  impostor bake. RGB is the albedo, ALPHA is the coverage.
                //   textured == 0  no texture: procedural shading from the colormap gradient by blade
                //                  height, nothing clipped. The mesh's own silhouette is the coverage,
                //                  which is what real grass geometry wants.
                float3 albedo;
                if (textured > 0.5)
                {
                    float4 tex = SAMPLE_TEXTURE2D(_LayerTex, sampler_LayerTex, i.uv);
                    if (coverageOnly > 0.5)
                    {
                        // Mask: the texture decides the silhouette, the gradient decides the color.
                        clip(dot(tex, coverageChannel) - _AlphaCutoff);
                        albedo = ColormapAlbedo(i.height);
                    }
                    else
                    {
                        clip(tex.a - _AlphaCutoff);
                        albedo = tex.rgb;
                    }
                }
                else
                {
                    albedo = ColormapAlbedo(i.height);
                }

                // Fake AO at the base. Foliage is mutually shadowed low down where blades/stems crowd
                // together, and darkening toward the ground is most of what reads as a contact shadow —
                // cheaper and steadier than any real occlusion term, and it grounds clumps that would
                // otherwise look like they float. Ramps from (1 - _BaseOcclusion) at the model base up
                // to full light by _BaseOcclusionReach, smoothed so the transition shows no band.
                // Applies to both shading paths, so textured foliage gets grounded the same way.
                float ao = lerp(1.0 - _BaseOcclusion, 1.0,
                                smoothstep(0.0, 1.0, saturate(i.height / max(_BaseOcclusionReach, 1e-3))));
                albedo *= ao;

                // Flattened (trampled) grass reads darker.
                albedo *= lerp(1.0, _DeformDarken, saturate(i.deform));

                // Debug LOD: tint by LOD level (green = near/LOD0, red = far/LOD1); still lit + clipped.
                if (_DebugLod > 0.5)
                    albedo = (lodLevel < 0.5) ? float3(0.1, 0.85, 0.1) : float3(0.9, 0.1, 0.1);

                // Soft two-sided foliage shading. We deliberately do NOT use a per-blade normal: the
                // screen-space-derivative normal was harsh and view-dependent (front-lit grass read
                // as a dark, opaque mass) and went NaN at grazing angles. Grass mostly catches
                // skylight from above, so shade with a world-up normal + wrapped diffuse + sky ambient
                // + two-sided translucency. The result is soft and consistent from any angle.
                float3 viewDir = normalize(_WorldSpaceCameraPos - i.worldPos);
                float3 n = float3(0.0, 1.0, 0.0);

                // Main directional light (no shadow sampling: this is a custom indirect-draw pass
                // where the shadowmap isn't reliably bound, which would otherwise zero the light).
                Light mainLight = GetMainLight();

                // Wrapped (half-Lambert) diffuse — soft, never crushes to black.
                float wrap = saturate(dot(n, mainLight.direction) * 0.5 + 0.5);
                float3 direct = albedo * mainLight.color * wrap;

                // Sky ambient fill (dominant, keeps the grass reading as a soft canopy).
                float3 ambient = albedo * SampleSH(n) * _AmbientStrength;

                // Two-sided translucency: strongest looking toward the sun through the blades
                // (backlit glow), plus a gentle base (_SssBase) so front-lit grass still looks airy
                // rather than opaque. Brightest at the tips (i.height).
                float back = saturate(dot(viewDir, -mainLight.direction));
                float trans = (pow(back, _SssPower) + _SssBase) * _SssIntensity * i.height;
                float3 subsurface = _SubsurfaceColor.rgb * trans * mainLight.color;

                float3 color = direct + ambient + subsurface;
                
                color = MixFog(color, i.fog);
                fragout.col = float4(color, 1.0);
                return fragout;
            }
            ENDHLSL
        }
    }
}
