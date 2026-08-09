using UnityEngine;
using UnityEngine.Assertions.Must;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

// Volumetric Lighting feature by Jonas Mortensen (jonas.mortensen@unity3d.com)
// ---------------------------------------------------------------------------------

public sealed class VolumetricFogRendererFeature : ScriptableRendererFeature
{
    #region FEATURE_FIELDS
    [SerializeField]
    [HideInInspector]
    private Material m_Material;

    private CustomPostRenderPass m_FullScreenPass;

    #endregion

    #region FEATURE_METHODS

    public override void Create()
    {
#if UNITY_EDITOR

        if (m_Material == null)
            m_Material = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/MyStuff/Volumetrics/VolumetricFog_Mat.mat");
#endif

        if(m_Material)
            m_FullScreenPass = new CustomPostRenderPass(name, m_Material);
    }

    // Here you can inject one or multiple render passes in the renderer.
    // This method is called when setting up the renderer once per-camera.
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        // Skip rendering if m_Material or the pass instance are null for whatever reason
        if (m_Material == null || m_FullScreenPass == null)
            return;

        // This check makes sure to not render the effect to reflection probes or preview cameras as post-processing is typically not desired there
        if (renderingData.cameraData.cameraType == CameraType.Preview)
            return;

        VolumetricFogVolumeComponent myVolume = VolumeManager.instance.stack?.GetComponent<VolumetricFogVolumeComponent>();
        if (myVolume == null || !myVolume.IsActive())
            return;

        m_FullScreenPass.renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        
        m_FullScreenPass.ConfigureInput(ScriptableRenderPassInput.Depth);

        renderer.EnqueuePass(m_FullScreenPass);
    }

    #endregion

    private class CustomPostRenderPass : ScriptableRenderPass
    {
        #region PASS_FIELDS

        // The material used to render the post-processing effect
        private Material m_Material;

        // The handle to the temporary color copy texture (only used in the non-render graph path)
        private RTHandle m_CopiedColor;

        // The property block used to set additional properties for the material
        private static MaterialPropertyBlock s_SharedPropertyBlock = new MaterialPropertyBlock();

        // This constant is meant to showcase how to create a copy color pass that is needed for most post-processing effects
        private static readonly bool kCopyActiveColor = false;

        // This constant is meant to showcase how you can add dept-stencil support to your main pass
        private static readonly bool kBindDepthStencilAttachment = false;

        // Creating some shader properties in advance as this is slightly more efficient than referencing them by string
        private static readonly int kBlitTexturePropertyId = Shader.PropertyToID("_BlitTexture");
        private static readonly int kBlitScaleBiasPropertyId = Shader.PropertyToID("_BlitScaleBias");

        #endregion

        public CustomPostRenderPass(string passName, Material material)
        {
            profilingSampler = new ProfilingSampler(passName);
            m_Material = material;

            // * The 'requiresIntermediateTexture' field needs to be set to 'true' when a ScriptableRenderPass intends to sample
            //   the active color buffer
            // * This will make sure that URP will not apply the optimization of rendering the entire frame to the write-only backbuffer,
            //   but will instead render to intermediate textures that can be sampled, which is typically needed for post-processing
            requiresIntermediateTexture = kCopyActiveColor;
        }

        #region PASS_SHARED_RENDERING_CODE

        // This method contains the shared rendering logic for doing the temporary color copy pass (used by both the non-render graph and render graph paths)
        private static void ExecuteCopyColorPass(RasterCommandBuffer cmd, RTHandle sourceTexture)
        {
            Blitter.BlitTexture(cmd, sourceTexture, new Vector4(1, 1, 0, 0), 0.0f, false);
        }

        // This method contains the shared rendering logic for doing the main post-processing pass (used by both the non-render graph and render graph paths)
        private static void ExecuteMainPass(RasterCommandBuffer cmd, RTHandle sourceTexture, Material material)
        {
            s_SharedPropertyBlock.Clear();
            if(sourceTexture != null)
                s_SharedPropertyBlock.SetTexture(kBlitTexturePropertyId, sourceTexture);

            // This uniform needs to be set for user materials with shaders relying on core Blit.hlsl to work as expected
            s_SharedPropertyBlock.SetVector(kBlitScaleBiasPropertyId, new Vector4(1, 1, 0, 0));
            
            VolumetricFogVolumeComponent myVolume = VolumeManager.instance.stack?.GetComponent<VolumetricFogVolumeComponent>();
            if (myVolume != null)
            {
                s_SharedPropertyBlock.SetFloat("_BackScattering", myVolume.backScattering.value);
                s_SharedPropertyBlock.SetFloat("_ForwardScattering", myVolume.forwardScattering.value);
                s_SharedPropertyBlock.SetColor("_ForwardScatterColor", myVolume.forwardScatterColor.value);
                s_SharedPropertyBlock.SetColor("_BackScatterColor", myVolume.backScatterColor.value);
                s_SharedPropertyBlock.SetFloat("_FogIntensity", myVolume.fogIntensity.value);
                s_SharedPropertyBlock.SetColor("_FogColor", myVolume.fogColor.value);
                s_SharedPropertyBlock.SetFloat("_Steps", myVolume.stepCount.value);
                s_SharedPropertyBlock.SetFloat("_Distance", myVolume.distance.value);
                
                LocalKeyword bayerOffsetKeyword = new LocalKeyword(material.shader, "USE_BAYER_OFFSET");
                material.SetKeyword(bayerOffsetKeyword, myVolume.bayerOffset.value);
            }
            cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1, s_SharedPropertyBlock);
        }

        // This method is used to get the descriptor used for creating the temporary color copy texture that will enable the main pass to sample the screen color
        private static RenderTextureDescriptor GetCopyPassTextureDescriptor(RenderTextureDescriptor desc)
        {
            // Unless 'desc.bindMS = true' for an MSAA texture a resolve pass will be inserted before it is bound for sampling.
            // Since our main pass shader does not expect to sample an MSAA target we will leave 'bindMS = false'.
            // If the camera target has MSAA enabled an MSAA resolve will still happen before our copy-color pass but
            // with this change we will avoid an unnecessary MSAA resolve before our main pass.
            desc.msaaSamples = 1;

            // This avoids copying the depth buffer tied to the current descriptor as the main pass in this example does not use it
            desc.depthBufferBits = (int)DepthBits.None;

            return desc;
        }

        #endregion

        #region PASS_NON_RENDER_GRAPH_PATH

        #endregion

        #region PASS_RENDER_GRAPH_PATH

        // The custom copy color pass data that will be passed at render graph execution to the lambda we set with "SetRenderFunc" during render graph setup
        private class CopyPassData
        {
            public TextureHandle inputTexture;
        }

        // The custom main pass data that will be passed at render graph execution to the lambda we set with "SetRenderFunc" during render graph setup
        private class MainPassData
        {
            public Material material;
            public TextureHandle inputTexture;
        }

        private static void ExecuteMainPass(MainPassData data, RasterGraphContext context)
        {
            ExecuteMainPass(context.cmd, data.inputTexture.IsValid() ? data.inputTexture : null, data.material);
        }

        // Here you can implement the rendering logic for the render graph path
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourcesData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalRenderer renderer = (UniversalRenderer) cameraData.renderer;

            using (var builder = renderGraph.AddRasterRenderPass<MainPassData>("Volumetrics Pass", out var passData, profilingSampler))
            {
                passData.material = m_Material;
                builder.SetRenderAttachment(resourcesData.activeColorTexture, 0, AccessFlags.Write);

                builder.SetRenderFunc((MainPassData data, RasterGraphContext context) =>
                {
                    ExecuteMainPass(data, context);
                });
            }
        }
        #endregion
    }
}

