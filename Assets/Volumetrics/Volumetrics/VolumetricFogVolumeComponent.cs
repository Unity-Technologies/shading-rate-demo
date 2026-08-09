using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[VolumeComponentMenu("Post-processing Custom/Volumetric Fog")]
[VolumeRequiresRendererFeatures(typeof(VolumetricFogRendererFeature))]
[SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
public sealed class VolumetricFogVolumeComponent : VolumeComponent, IPostProcessComponent
{
    public VolumetricFogVolumeComponent()
    {
        displayName = "Volumetric Fog";
    }
    
    [Header("God Rays")]
    
    [Tooltip("Intensity of the god-rays when looking at the sun")]
    public ClampedFloatParameter forwardScattering = new ClampedFloatParameter(0, 0, 5);
    
    [Tooltip("Color of the god-rays when looking at the sun")]
    public ColorParameter forwardScatterColor = new ColorParameter(Color.white);
    
    [Tooltip("Intensity of the god-rays when looking away from the sun")]
    public ClampedFloatParameter backScattering = new ClampedFloatParameter(0, 0, 5);
    
    [Tooltip("Color of the god-rays when looking away from the sun")]
    public ColorParameter backScatterColor = new ColorParameter(Color.white);
    
    [Header("Fog")]
    [Tooltip("Intensity of the height fog")]
    public ClampedFloatParameter fogIntensity = new ClampedFloatParameter(0, 0, 5);
    
    [Tooltip("Color of the height fog")]
    public ColorParameter fogColor = new ColorParameter(Color.white);
    
    [Header("Quality")]
    [Tooltip("Controls how many points to sample along the ray")]
    public IntParameter stepCount = new IntParameter(16);
    
    [Tooltip("Controls how far the ray will travel")]
    public FloatParameter distance = new FloatParameter(1000);
    
    [Tooltip("Whether to use the bayer dither pattern to offset rays. Allows for smaller step counts but introduces dithering.")]
    public BoolParameter bayerOffset = new BoolParameter(true);
    
    public bool IsActive()
    {
        return backScattering.value > 0 || forwardScattering.value > 0 || fogIntensity.value > 0;
    }
}
