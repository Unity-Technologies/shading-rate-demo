using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

// Draws GPU-indirect foliage. The scene's GrasslandVolume owns one grass layer plus N detail layers;
// each supplies CPU-baked instance positions (from its mask) and, per LOD (near/far), an imported Mesh
// and optionally an RGBA texture. This pass uploads each layer's positions + per-LOD mesh geometry,
// then per frame: one ComputeDeform (flatten trail), then per layer a ComputeCulling dispatch that
// ring-culls vs the camera and routes into two LOD append buffers, and finally one
// DrawProceduralIndirect per (layer x LOD) binding that LOD's mesh and texture. LODs/layers with no
// mesh or no instances are skipped. A LOD with a texture is shaded from it (RGB = albedo, A = coverage,
// alpha-clipped); a LOD without one is shaded procedurally from the material's colormap gradient.
public class ProceduralRenderingFeature : ScriptableRendererFeature
{
    [Tooltip("Foliage material (indirect ParticleShader). All other config is on the GrasslandVolume.")]
    [SerializeField] private Material material;
    private ProceduralPass proceduralRenderPass;

    public override void Create()
    {
        if (material == null) return;
        proceduralRenderPass = new ProceduralPass(material);
        proceduralRenderPass.renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderData)
    {
        if (proceduralRenderPass == null) return;
        if ((renderData.cameraData.cameraType == CameraType.Game) || (renderData.cameraData.cameraType == CameraType.SceneView))
            renderer.EnqueuePass(proceduralRenderPass);
    }

    protected override void Dispose(bool disposing)
    {
        if (proceduralRenderPass != null) proceduralRenderPass.DisposeResources();
    }
}

class ProceduralPass : ScriptableRenderPass
{
    private const int LODS = 2;              // near (dense) + far (sparse) per layer

    private Material material;

    private GrasslandVolume volume;
    private int builtVersion = -1;
    private bool initialized;
    private float worldScale = 1.0f;

    // GPU state for one foliage layer: its baked positions + per-LOD geometry/cull/draw buffers.
    private class LayerGPU
    {
        public ComputeBuffer positions;     // world x,y,z + yaw (from the volume's placement bake)
        public int count;
        public int cullGroups;
        public float lodDistance;
        public float windScale;
        public Vector3[] clumpScale = new Vector3[LODS];   // per LOD: the far one carries impostorScale
        public float heightVariance;
        public float alphaCutoff;
        public float deformCull;       // >0 = Remove response: cull at this trail strength
        public float deformShrinkAt;   // what the SHADER gets: same threshold, or 0 to skip the shrink
        // "Look at" resolved from the layer's direction: yaw/pitch in radians, tilt already damped.
        public float lookAtEnabled, lookAtYaw, lookAtPitch, lookAtVariance;

        public float coverageOnly;                          // 1 = textures are masks, color from gradient
        public Vector4[] coverageChannel = new Vector4[LODS];

        public bool[] active = new bool[LODS];
        public Texture2D[] texture = new Texture2D[LODS];   // per-LOD texture (null = procedural)
        public float[] height = new float[LODS];
        public int[] vcount = new int[LODS];                // per-LOD mesh vertex count (for the debug HUD)
        public ComputeBuffer[] vertex = new ComputeBuffer[LODS];
        public ComputeBuffer[] uv = new ComputeBuffer[LODS];
        public GraphicsBuffer[] index = new GraphicsBuffer[LODS];
        public ComputeBuffer[] culled = new ComputeBuffer[LODS];    // Append, one per LOD
        public ComputeBuffer[] indirect = new ComputeBuffer[LODS];
        public MaterialPropertyBlock[] mpb = new MaterialPropertyBlock[LODS];
    }
    private LayerGPU[] layers = new LayerGPU[0];

    // Shader property IDs
    private int vertexBufferUID, uvBufferUID, culledPositionsBufferUID;
    private int positionsBufferUID, culled0UID, culled1UID;
    private int numInstancesUID, cullOriginUID, cullRadiusUID, cullEnabledUID, lodDistanceUID, lodBlendUID;
    private int frustumPlanesUID, frustumPaddingUID;
    private int fieldOriginUID, terrainSizeUID;
    private int lodCenterUID, lodBandWidthUID, debugLodUID;
    private int modelHeightUID, worldScaleUID, timeUID, windUID, windScaleUID, clumpScaleUID, heightVarianceUID, lodLevelUID;
    private int layerTexUID, alphaCutoffUID, texturedUID, coverageOnlyUID, coverageChannelUID;
    private int lookAtEnabledUID, lookAtYawUID, lookAtPitchUID, lookAtVarianceUID;
    // Deformation trail
    private int deformTexUID, deformResUID, deformDecayUID, repulsorPosUID, repulsorRadiusUID;
    private int deformTexMatUID, deformFlattenUID, deformCullUID;

    private ComputeShader computeShader;
    private int kCulling, kDeform;
    private Matrix4x4 identity = Matrix4x4.identity;

    private RenderTexture deformTex;
    private int deformRes = 512;
    private int deformGroups = 1;

    private const int MaxRepulsors = 8;                // must match MAX_REPULSORS in the compute shader
    private readonly Vector4[] _repulsors = new Vector4[MaxRepulsors];
    private readonly uint[] _statArgs = new uint[5];   // scratch for indirect-args readback (debug HUD)
    private readonly Plane[] _frustum = new Plane[6];  // scratch for frustum culling
    private readonly Vector4[] _frustumV4 = new Vector4[6];

    public ProceduralPass(Material material)
    {
        this.material = material;
        computeShader = (ComputeShader)Resources.Load("ComputeParticleDeformation");
        kCulling = computeShader.FindKernel("ComputeCulling");
        kDeform = computeShader.FindKernel("ComputeDeform");
        CacheShaderIDs();
    }

    private void CacheShaderIDs()
    {
        vertexBufferUID = Shader.PropertyToID("vertexBuffer");
        uvBufferUID = Shader.PropertyToID("uvBuffer");
        culledPositionsBufferUID = Shader.PropertyToID("culledPositionsBuffer");
        positionsBufferUID = Shader.PropertyToID("positionsBuffer");
        culled0UID = Shader.PropertyToID("culledPositions0");
        culled1UID = Shader.PropertyToID("culledPositions1");
        numInstancesUID = Shader.PropertyToID("numInstances");
        cullOriginUID = Shader.PropertyToID("cullOrigin");
        cullRadiusUID = Shader.PropertyToID("cullRadius");
        cullEnabledUID = Shader.PropertyToID("cullEnabled");
        lodDistanceUID = Shader.PropertyToID("lodDistance");
        lodBlendUID = Shader.PropertyToID("lodBlend");
        frustumPlanesUID = Shader.PropertyToID("frustumPlanes");
        frustumPaddingUID = Shader.PropertyToID("frustumPadding");
        fieldOriginUID = Shader.PropertyToID("fieldOrigin");
        terrainSizeUID = Shader.PropertyToID("terrainSize");
        lodCenterUID = Shader.PropertyToID("lodCenter");
        lodBandWidthUID = Shader.PropertyToID("lodBandWidth");
        debugLodUID = Shader.PropertyToID("_DebugLod");
        modelHeightUID = Shader.PropertyToID("modelHeight");
        worldScaleUID = Shader.PropertyToID("worldScale");
        timeUID = Shader.PropertyToID("time");
        windUID = Shader.PropertyToID("wind");
        windScaleUID = Shader.PropertyToID("windScale");
        clumpScaleUID = Shader.PropertyToID("clumpScale");
        heightVarianceUID = Shader.PropertyToID("heightVariance");
        lodLevelUID = Shader.PropertyToID("lodLevel");
        layerTexUID = Shader.PropertyToID("_LayerTex");
        alphaCutoffUID = Shader.PropertyToID("_AlphaCutoff");
        texturedUID = Shader.PropertyToID("textured");
        coverageOnlyUID = Shader.PropertyToID("coverageOnly");
        coverageChannelUID = Shader.PropertyToID("coverageChannel");
        lookAtEnabledUID = Shader.PropertyToID("lookAtEnabled");
        lookAtYawUID = Shader.PropertyToID("lookAtYaw");
        lookAtPitchUID = Shader.PropertyToID("lookAtPitch");
        lookAtVarianceUID = Shader.PropertyToID("lookAtVariance");

        deformTexUID = Shader.PropertyToID("deformTex");
        deformResUID = Shader.PropertyToID("deformRes");
        deformDecayUID = Shader.PropertyToID("deformDecay");
        repulsorPosUID = Shader.PropertyToID("repulsorPositions");
        repulsorRadiusUID = Shader.PropertyToID("repulsorCount");
        deformTexMatUID = Shader.PropertyToID("_DeformTex");
        deformFlattenUID = Shader.PropertyToID("deformFlatten");
        deformCullUID = Shader.PropertyToID("deformCull");
    }

    public void Initialize(GrasslandVolume v)
    {
        volume = v;
        worldScale = v.WorldExtentX;

        int n = v.LayerCount;
        layers = new LayerGPU[n];
        for (int li = 0; li < n; li++)
        {
            var layer = v.Layer(li);
            var layerData = new LayerGPU();
            layers[li] = layerData;
            if (layer == null) { layerData.count = 0; continue; }

            Vector4[] arr = v.LayerPositions(li) ?? System.Array.Empty<Vector4>();
            layerData.count = arr.Length;
            layerData.lodDistance = layer.lodDistance;
            layerData.windScale = layer.windScale;
            // The impostor stands in for geometry it can't reproduce, so it gets its own widening on
            // top of the layer's clump scale. Baked in here rather than in the shader — it's constant
            // per draw, and keeping it out of the vertex path means the near LOD is untouched.
            layerData.clumpScale[0] = layer.clumpScale;
            layerData.clumpScale[1] = Vector3.Scale(layer.clumpScale, layer.impostorScale);
            layerData.heightVariance = layer.heightVariance;
            layerData.alphaCutoff = layer.alphaCutoff;
            layerData.coverageOnly = layer.textureUse == TextureUse.CoverageOnly ? 1.0f : 0.0f;
            layerData.deformCull = layer.deformResponse == DeformResponse.Remove
                ? Mathf.Clamp(layer.deformRemoveAt, 0.05f, 1.0f)
                : 0.0f;
            // The compute pass always culls at the threshold. The shader only gets it when the layer
            // wants the shrink — sending 0 puts it on the flatten path instead, and flatten strength is
            // already 0 for a Remove layer, so the model is left completely undeformed until it's culled.
            layerData.deformShrinkAt = layer.shrinkOnRemove ? layerData.deformCull : 0.0f;

            // Resolve the look-at direction into a yaw + a damped pitch once, here, rather than per
            // vertex. Elevation is clamped to the upper hemisphere so a sun below the horizon just
            // stops tilting instead of driving the plants into the ground.
            layerData.lookAtEnabled = layer.lookAt ? 1.0f : 0.0f;
            Vector3 dir = layer.lookAtDirection;
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward;
            dir.Normalize();
            layerData.lookAtYaw = Mathf.Atan2(dir.x, dir.z);
            layerData.lookAtPitch = Mathf.Asin(Mathf.Clamp01(dir.y)) * Mathf.Clamp01(layer.lookAtTilt);
            layerData.lookAtVariance = layer.lookAtVariance * Mathf.Deg2Rad;

            layerData.positions = new ComputeBuffer(Mathf.Max(1, layerData.count), sizeof(float) * 4);
            if (layerData.count > 0) layerData.positions.SetData(arr);
            layerData.cullGroups = Mathf.Max(1, Mathf.CeilToInt(layerData.count / 64.0f));

            for (int L = 0; L < LODS; L++)
            {
                layerData.mpb[L] = new MaterialPropertyBlock();
                layerData.culled[L] = new ComputeBuffer(Mathf.Max(1, layerData.count), sizeof(float) * 4,
                    ComputeBufferType.Append | ComputeBufferType.Structured);
                layerData.indirect[L] = new ComputeBuffer(5, sizeof(uint),
                    ComputeBufferType.IndirectArguments | ComputeBufferType.Structured);

                var mesh = L == 0 ? layer.nearMesh : layer.farMesh;
                layerData.texture[L] = L == 0 ? layer.nearTexture : layer.impostorTexture;
                layerData.coverageChannel[L] = CoverageChannel(layerData.texture[L]);
                layerData.active[L] = mesh != null && mesh.vertexCount > 0;
                if (!layerData.active[L]) continue;

                Vector3[] verts = mesh.vertices;
                int[] indices = mesh.triangles;
                if (indices.Length == 0) { layerData.active[L] = false; continue; }

                layerData.height[L] = Mathf.Max(0.01f, mesh.bounds.size.y);
                int vcount = verts.Length;
                layerData.vcount[L] = vcount;
                Vector2[] uvs = (mesh.uv != null && mesh.uv.Length == vcount) ? mesh.uv : new Vector2[vcount];

                layerData.vertex[L] = new ComputeBuffer(vcount, sizeof(float) * 3);
                layerData.uv[L] = new ComputeBuffer(vcount, sizeof(float) * 2);
                layerData.index[L] = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.Index, indices.Length, sizeof(int));

                layerData.vertex[L].SetData(verts);
                layerData.uv[L].SetData(uvs);
                layerData.index[L].SetData(indices);
                layerData.indirect[L].SetData(new uint[] { (uint)indices.Length, 0, 0, 0, 0 });
            }
        }

        // Persistent deformation trail texture 
        deformRes = Mathf.Clamp(v.deformResolution, 16, 2048);
        deformGroups = Mathf.Max(1, Mathf.CeilToInt(deformRes / 8.0f));
        deformTex = new RenderTexture(deformRes, deformRes, 0, RenderTextureFormat.RFloat)
        {
            enableRandomWrite = true,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };
        deformTex.Create();
        var prevRT = RenderTexture.active;
        RenderTexture.active = deformTex;
        GL.Clear(false, true, Color.clear);
        RenderTexture.active = prevRT;
    }

    private class PassData
    {
        public TextureHandle color;
        public TextureHandle depth;
        public Camera cam;
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        var v = GrasslandVolume.Active;
        if (v == null || material == null) return;

        if (!initialized || v != volume || v.ConfigVersion != builtVersion)
        {
            if (initialized) DisposeResources();
            Initialize(v);
            builtVersion = v.ConfigVersion;
            initialized = true;
        }

        // Debug HUD: read last frame's drawn instance counts back from the indirect args buffers.
        if (v.debugHud) UpdateStats();

        UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

        using (var builder = renderGraph.AddUnsafePass<PassData>("ProceduralDraw", out var passData))
        {
            passData.color = resourceData.activeColorTexture;
            passData.depth = resourceData.activeDepthTexture;
            passData.cam = cameraData.camera;

            builder.UseTexture(passData.color, AccessFlags.ReadWrite);
            builder.UseTexture(passData.depth, AccessFlags.ReadWrite);
            builder.AllowPassCulling(false);

            builder.SetRenderFunc((PassData data, UnsafeGraphContext ctx) => ExecutePass(data, ctx));
        }
    }

    private void ExecutePass(PassData data, UnsafeGraphContext ctx)
    {
        CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
        cmd.SetRenderTarget(data.color, data.depth);

        Vector2 fo = volume.FieldOriginXZ;
        // Cull ring + LOD select are centered on the rendering camera (works with no ball, and lets
        // the SceneView camera preview LOD). The ball is optional and only drives the deform trail.
        Vector3 camPos = data.cam != null ? data.cam.transform.position : Vector3.zero;
        // Gather the trail stampers (one per wheel). The array is always sent at full length — Unity
        // requires the array passed to SetComputeVectorArrayParam to match the declared size — with
        // radius 0 marking the unused slots and repulsorCount bounding the loop.
        int repulsorCount = 0;
        System.Array.Clear(_repulsors, 0, _repulsors.Length);
        if (volume.repulsors != null)
        {
            float radius = Mathf.Max(0.0f, volume.repulsorRadius);
            for (int i = 0; i < volume.repulsors.Length && repulsorCount < MaxRepulsors; i++)
            {
                var t = volume.repulsors[i];
                if (t == null) continue;
                Vector3 p = t.position;
                _repulsors[repulsorCount++] = new Vector4(p.x, p.y, p.z, radius);
            }
        }

        cmd.SetComputeIntParam(computeShader, deformResUID, deformRes);
        cmd.SetComputeFloatParam(computeShader, deformDecayUID, Time.deltaTime / Mathf.Max(0.01f, volume.deformClearTime));
        cmd.SetComputeFloatParam(computeShader, terrainSizeUID, volume.TerrainExtent);
        cmd.SetComputeVectorParam(computeShader, fieldOriginUID, new Vector4(fo.x, fo.y, 0, 0));
        cmd.SetComputeVectorArrayParam(computeShader, repulsorPosUID, _repulsors);
        cmd.SetComputeIntParam(computeShader, repulsorRadiusUID, repulsorCount);
        cmd.SetComputeTextureParam(computeShader, kDeform, deformTexUID, deformTex);
        cmd.DispatchCompute(computeShader, kDeform, deformGroups, deformGroups, 1);

        // GPU cull and LOD selection uniforms
        cmd.SetComputeVectorParam(computeShader, cullOriginUID, new Vector4(camPos.x, camPos.y, camPos.z, 0.0f));
        cmd.SetComputeFloatParam(computeShader, cullRadiusUID, volume.cullRadius);
        cmd.SetComputeIntParam(computeShader, cullEnabledUID, volume.gpuCulling ? 1 : 0);
        cmd.SetComputeFloatParam(computeShader, lodBlendUID, volume.lodBlend);

        // Camera frustum planes for GPU culling 
        if (data.cam != null)
        {
            GeometryUtility.CalculateFrustumPlanes(data.cam, _frustum);
            for (int p = 0; p < 6; p++)
            {
                Plane frstm = _frustum[p];
                _frustumV4[p] = new Vector4(frstm.normal.x, frstm.normal.y, frstm.normal.z, frstm.distance);
            }
            cmd.SetComputeVectorArrayParam(computeShader, frustumPlanesUID, _frustumV4);
            cmd.SetComputeFloatParam(computeShader, frustumPaddingUID, volume.cullPadding);
        }

        for (int li = 0; li < layers.Length; li++)
        {
            var layer = layers[li];
            if (layer == null || layer.count == 0 || !IsVisible(li)) continue;

            layer.culled[0].SetCounterValue(0);
            layer.culled[1].SetCounterValue(0);

            // Indirect args buffer
            cmd.SetComputeIntParam(computeShader, numInstancesUID, layer.count);
            cmd.SetComputeFloatParam(computeShader, lodDistanceUID, layer.lodDistance);
            cmd.SetComputeFloatParam(computeShader, deformCullUID, layer.deformCull);
            // The culling kernel reads the trail written by ComputeDeform earlier in this same command
            // buffer, so Remove layers cull against the current frame's tracks.
            cmd.SetComputeTextureParam(computeShader, kCulling, deformTexUID, deformTex);
            cmd.SetComputeBufferParam(computeShader, kCulling, positionsBufferUID, layer.positions);
            cmd.SetComputeBufferParam(computeShader, kCulling, culled0UID, layer.culled[0]);
            cmd.SetComputeBufferParam(computeShader, kCulling, culled1UID, layer.culled[1]);
            cmd.DispatchCompute(computeShader, kCulling, layer.cullGroups, 1, 1);

            for (int L = 0; L < LODS; L++)
                if (layer.active[L]) cmd.CopyCounterValue(layer.culled[L], layer.indirect[L], 4);
        }

        // Draw uniforms 
        material.SetFloat(worldScaleUID, worldScale);
        material.SetFloat(timeUID, Time.time);
        material.SetVector(windUID, volume.wind);
        material.SetVector(lodCenterUID, new Vector4(camPos.x, camPos.y, camPos.z, 0.0f));
        material.SetFloat(lodBandWidthUID, volume.lodBlend);
        material.SetFloat(debugLodUID, volume.debugLod ? 1.0f : 0.0f);
        material.SetTexture(deformTexMatUID, deformTex);
        material.SetVector(fieldOriginUID, new Vector4(fo.x, fo.y, 0, 0));

        // One indirect draw per active layer/LOD
        for (int li = 0; li < layers.Length; li++)
        {
            var layer = layers[li];
            if (layer == null || layer.count == 0 || !IsVisible(li)) continue;

            for (int L = 0; L < LODS; L++)
            {
                if (!layer.active[L]) continue;
                var mpb = layer.mpb[L];
                mpb.SetFloat(lodLevelUID, L);                 // 0 = near, 1 = far
                mpb.SetFloat(modelHeightUID, Mathf.Max(0.01f, layer.height[L]));
                mpb.SetFloat(windScaleUID, layer.windScale);
                mpb.SetVector(clumpScaleUID, layer.clumpScale[L]);
                mpb.SetFloat(heightVarianceUID, layer.heightVariance);
                mpb.SetFloat(lodDistanceUID, layer.lodDistance);
                mpb.SetFloat(lookAtEnabledUID, layer.lookAtEnabled);
                mpb.SetFloat(lookAtYawUID, layer.lookAtYaw);
                mpb.SetFloat(lookAtPitchUID, layer.lookAtPitch);
                mpb.SetFloat(lookAtVarianceUID, layer.lookAtVariance);
                // Deform response is per-layer now, so flatten strength moves to the MPB: a Remove
                // layer gets no flatten and a cull threshold, a Flatten layer the reverse.
                mpb.SetFloat(deformCullUID, layer.deformShrinkAt);
                mpb.SetFloat(deformFlattenUID, layer.deformCull > 0.0f ? 0.0f : volume.deformStrength);
                mpb.SetBuffer(vertexBufferUID, layer.vertex[L]);
                mpb.SetBuffer(uvBufferUID, layer.uv[L]);
                mpb.SetBuffer(culledPositionsBufferUID, layer.culled[L]);
                bool textured = layer.texture[L] != null;
                mpb.SetFloat(texturedUID, textured ? 1.0f : 0.0f);
                mpb.SetFloat(coverageOnlyUID, layer.coverageOnly);
                mpb.SetVector(coverageChannelUID, layer.coverageChannel[L]);
                if (textured) mpb.SetTexture(layerTexUID, layer.texture[L]);
                mpb.SetFloat(alphaCutoffUID, layer.alphaCutoff);
                cmd.DrawProceduralIndirect(layer.index[L], identity, material, 0, MeshTopology.Triangles, layer.indirect[L], 0, mpb);
            }
        }
    }

    // Which channels of a coverage mask carry the coverage, decided from the texture's own format so
    // the author doesn't have to declare it: a texture with an alpha channel uses alpha, one without
    // (a white-on-black silhouette bake, say) uses RGB luminance.
    private static Vector4 CoverageChannel(Texture2D tex)
    {
        if (tex == null) return new Vector4(0, 0, 0, 1);
        bool hasAlpha = UnityEngine.Experimental.Rendering.GraphicsFormatUtility.HasAlphaChannel(tex.graphicsFormat);
        return hasAlpha ? new Vector4(0, 0, 0, 1)
                        : new Vector4(1.0f / 3.0f, 1.0f / 3.0f, 1.0f / 3.0f, 0.0f);
    }

    // Per-layer visibility
    private bool IsVisible(int li)
    {
        if (volume == null || li >= volume.LayerCount) return true;
        var lv = volume.Layer(li);
        return lv == null || lv.visible;
    }

    // Debug HUD
    private void UpdateStats()
    {
        GrasslandStats.Reset();
        if (layers == null) return;
        for (int li = 0; li < layers.Length; li++)
        {
            var lg = layers[li];
            // Hidden layers dispatch nothing, so their indirect args hold a stale count — skip them
            // or the HUD keeps reporting instances that aren't being drawn.
            if (lg == null || lg.count == 0 || !IsVisible(li)) continue;
            for (int L = 0; L < LODS; L++)
            {
                if (!lg.active[L] || lg.indirect[L] == null) continue;
                lg.indirect[L].GetData(_statArgs);
                int inst = (int)_statArgs[1];
                GrasslandStats.Instances[L] += inst;
                GrasslandStats.Vertices[L] += (long)inst * lg.vcount[L];
            }
        }
    }

    public void DisposeResources()
    {
        void Rel(ComputeBuffer b) { if (b != null) b.Release(); }
        if (layers != null)
            foreach (var layer in layers)
            {
                if (layer == null) continue;
                Rel(layer.positions); layer.positions = null;
                for (int L = 0; L < LODS; L++)
                {
                    Rel(layer.vertex[L]); layer.vertex[L] = null;
                    Rel(layer.uv[L]); layer.uv[L] = null;
                    if (layer.index[L] != null) { layer.index[L].Release(); layer.index[L] = null; }
                    Rel(layer.culled[L]); layer.culled[L] = null;
                    Rel(layer.indirect[L]); layer.indirect[L] = null;
                    layer.active[L] = false;
                }
            }
        layers = new LayerGPU[0];
        if (deformTex != null) { deformTex.Release(); deformTex = null; }
        initialized = false;
    }
}
