using UnityEngine;

// A baked grass clump: the geometry of one clump (a grid of bent blades), authored in the LOD
// scene via GrassClumpAuthoring and assigned to a GrasslandVolume LOD slot. The render feature
// uploads these arrays straight into GPU buffers and draws them indirectly per LOD.
[CreateAssetMenu(fileName = "GrassClump", menuName = "Grassland/Grass Clump", order = 0)]
public class GrassClumpAsset : ScriptableObject
{
    [Header("Baked geometry")]
    public Vector3[] vertices;
    public Vector2[] uvs;
    public int[] indices;

    [Header("Metadata")]
    [Tooltip("Number of blade instances in this clump.")]
    public int bladeCount;
    [Tooltip("Blade height (meters) this clump was baked at; drives wind falloff at runtime.")]
    public float height = 2.5f;

    [Header("Impostor")]
    [Tooltip("If true, the geometry is a billboard mesh textured with 'atlas' (a captured image of " +
             "the clump), drawn alpha-clipped. Cheapest far LOD.")]
    public bool isImpostor;
    [Tooltip("Captured clump atlas (RGB = color, A = coverage). Usually stored as a sub-asset.")]
    public Texture2D atlas;

    [Header("Authoring params (for re-bake / reference)")]
    public Mesh sourceBlade;
    public Vector2Int density = new Vector2Int(16, 16);
    public float cellSize = 6.0f;
    public float bladeWidthScale = 1.0f;
    public Vector4 seed = new Vector4(4.58f, 3.25f, -1.63f, 4.29f);

    public bool IsValid => vertices != null && vertices.Length > 0 && indices != null && indices.Length > 0;
}
