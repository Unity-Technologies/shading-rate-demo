using UnityEngine;
using UnityEngine.Experimental.Rendering;

// Fallback heightfield provider + bake helper for GrasslandVolume.
//
// The authoritative terrain now lives on a GrasslandVolume in the scene (which owns the
// heightmap texture, world bounds and the baked RFloat heightmap). This class is the
// procedural fallback used when no heightmap texture is assigned, plus the shared baker that
// turns any per-texel height source into the R32_SFloat texture the grass compute samples.
public static class GrasslandUtil
{
    // fBm shape constants (size-independent: sampled in normalized [0,1] field space).
    public const float Frequency = 1.4f;   // base frequency of the hills across the field
    private const int Octaves = 4;
    private static readonly Vector2 Offset = new Vector2(13.7f, 91.2f);

    // Normalized fBm height in [0,1] at a normalized field coordinate (u,v) in [0,1].
    // The volume multiplies this by its heightScale (meters).
    public static float SampleHeightNormalized(float u, float v)
    {
        float h = 0.0f;
        float amp = 0.5f;
        float freq = Frequency;
        float norm = 0.0f;
        for (int o = 0; o < Octaves; o++)
        {
            h += amp * Mathf.PerlinNoise(Offset.x + u * freq, Offset.y + v * freq);
            norm += amp;
            freq *= 2.0f;
            amp *= 0.5f;
        }
        return norm > 0.0f ? h / norm : 0.0f;
    }

    // Bake an RFloat (R32_SFloat, meters) heightmap that both the CPU terrain mesh and the
    // placement bake sample. heightAt(u,v) returns meters for a normalized [0,1] field coord.
    public delegate float HeightSampler(float u, float v);

    public static Texture2D BakeToRFloat(int resolution, HeightSampler heightAt, string name = "GrasslandHeightmap")
    {
        resolution = Mathf.Max(2, resolution);
        var tex = new Texture2D(resolution, resolution, GraphicsFormat.R32_SFloat,
            TextureCreationFlags.None);
        tex.name = name;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;

        var px = new Color[resolution * resolution];
        for (int y = 0; y < resolution; y++)
        {
            float v = (y + 0.5f) / resolution;
            for (int x = 0; x < resolution; x++)
            {
                float u = (x + 0.5f) / resolution;
                px[y * resolution + x] = new Color(heightAt(u, v), 0, 0, 0);
            }
        }
        tex.SetPixels(px);
        tex.Apply(false, false);
        return tex;
    }
}
