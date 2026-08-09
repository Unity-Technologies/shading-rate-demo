using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

// CPU generator for a grass clump: one blade model instantiated into a countX*countY grid, each
// blade bent along a randomized quadratic bezier + random yaw, blades widened by widthScale.
//
// This is the single source of clump geometry. It is a C# port of the compute kernels
// ComputeCurves + ComputeGeometry that used to bake clumps at runtime. Both the authoring preview
// (GrassClumpAuthoring) and the offline bake (GrassClumpAsset) use it, so the previewed clump is
// exactly what gets baked and later drawn by the render feature.
public static class ClumpBuilder
{
    public struct Geometry
    {
        public Vector3[] vertices;
        public Vector2[] uvs;
        public int[] indices;
        public int bladeCount;
        public float height;
    }

    private static float Frac(float x) => x - Mathf.Floor(x);
    private static float Random2D(Vector2 xy, Vector2 dir) => Frac(9.15f * Mathf.Sin(Vector2.Dot(xy, dir)));

    private static Vector3 Bezier(float t, Vector3 a, Vector3 b, Vector3 c)
    {
        float u = 1.0f - t;
        return u * u * a + 2.0f * u * t * b + t * t * c;
    }

    // Read a mesh's positions/uvs/indices without requiring Read/Write (works on imported .obj).
    public static bool ReadMesh(Mesh mesh, out Vector3[] positions, out Vector2[] uvs, out int[] indices)
    {
        positions = null; uvs = null; indices = null;
        if (mesh == null) return false;

        using (var dataArray = Mesh.AcquireReadOnlyMeshData(mesh))
        {
            var data = dataArray[0];
            int vc = mesh.vertexCount;
            int ic = (int)mesh.GetIndexCount(0);
            var p = new NativeArray<Vector3>(vc, Allocator.Temp);
            var u = new NativeArray<Vector2>(vc, Allocator.Temp);
            var idx = new NativeArray<int>(ic, Allocator.Temp);

            data.GetVertices(p);
            if (data.HasVertexAttribute(VertexAttribute.TexCoord0)) data.GetUVs(0, u);
            data.GetIndices(idx, 0);

            positions = p.ToArray();
            uvs = u.ToArray();
            indices = idx.ToArray();

            p.Dispose(); u.Dispose(); idx.Dispose();
        }
        return true;
    }

    // Build a clump from an already-read blade. The blade is uniformly scaled so its height == height.
    public static Geometry Build(Vector3[] bladePos, Vector2[] bladeUV, int[] bladeIdx,
        int countX, int countY, float cellX, float cellZ, float height, float widthScale, Vector4 seed)
    {
        countX = Mathf.Max(1, countX);
        countY = Mathf.Max(1, countY);
        height = Mathf.Max(1e-3f, height);
        int modelSize = bladePos.Length;
        int modelIdx = bladeIdx.Length;

        // Uniformly scale the blade so its top reaches 'height' (so the bezier bend spans it).
        float rawMax = 0.0f;
        for (int i = 0; i < modelSize; i++) rawMax = Mathf.Max(rawMax, bladePos[i].y);
        float s = rawMax > 1e-4f ? height / rawMax : 1.0f;
        var blade = new Vector3[modelSize];
        for (int i = 0; i < modelSize; i++) blade[i] = bladePos[i] * s;

        int bladeCount = countX * countY;
        var verts = new Vector3[bladeCount * modelSize];
        var uvs = new Vector2[bladeCount * modelSize];
        var indices = new int[bladeCount * modelIdx];

        Vector2 sxz = new Vector2(seed.x, seed.z);
        Vector2 sxy = new Vector2(seed.x, seed.y);
        Vector2 swz = new Vector2(seed.w, seed.z);

        for (int iy = 0; iy < countY; iy++)
        {
            for (int ix = 0; ix < countX; ix++)
            {
                int instanceID = iy * countX + ix;
                int vBase = instanceID * modelSize;
                int iBase = instanceID * modelIdx;

                float x = -cellX * 0.5f + cellX * ((float)ix / countX);
                float z = -cellZ * 0.5f + cellZ * ((float)iy / countY);
                Vector2 uv = new Vector2((float)ix / countX, (float)iy / countY);

                Vector3 cp0 = new Vector3(x, 0.0f, z);
                Vector3 cp1 = new Vector3(x, height * 0.5f, z);
                Vector3 cp2 = new Vector3(x, height, z);

                Vector3 rndm = new Vector3(Random2D(uv, sxz), Random2D(uv, sxy), Random2D(uv, swz)) * 2.0f - Vector3.one;
                cp0 += new Vector3(rndm.x * 0.1f, 0.0f, rndm.z * 0.1f);
                cp1 += new Vector3(rndm.y, 0.0f, rndm.x) * 1.5f;
                cp2 += new Vector3(rndm.z * 3.0f, -(rndm.y * 0.5f + 0.5f) * height, rndm.y * 2.0f) * 2.0f;

                float angle = Mathf.Acos(2.0f * Random2D(uv, sxz) - 1.0f) * 5.0f;
                float ca = Mathf.Cos(angle), sa = Mathf.Sin(angle);

                for (int i = 0; i < modelSize; i++)
                {
                    Vector3 vPos = blade[i];
                    vPos.x *= widthScale;

                    Vector3 t = Bezier(vPos.y / height, cp0, cp1, cp2);
                    Vector3 wp = vPos + t;
                    Vector3 p = wp - cp0;
                    // Y-axis rotation by 'angle', then re-anchor at the blade base cp0.
                    Vector3 rp = new Vector3(ca * p.x + sa * p.z, p.y, -sa * p.x + ca * p.z) + cp0;

                    verts[vBase + i] = rp;
                    uvs[vBase + i] = (bladeUV != null && bladeUV.Length == modelSize) ? bladeUV[i] : Vector2.zero;
                }

                for (int j = 0; j < modelIdx; j++)
                    indices[iBase + j] = instanceID * modelSize + bladeIdx[j];
            }
        }

        return new Geometry { vertices = verts, uvs = uvs, indices = indices, bladeCount = bladeCount, height = height };
    }

    // Convenience: read a blade mesh then build a clump.
    public static bool Build(Mesh bladeMesh, int countX, int countY, float cellX, float cellZ,
        float height, float widthScale, Vector4 seed, out Geometry geometry)
    {
        geometry = default;
        if (!ReadMesh(bladeMesh, out var pos, out var uv, out var idx)) return false;
        geometry = Build(pos, uv, idx, countX, countY, cellX, cellZ, height, widthScale, seed);
        return true;
    }

    // Build an "X" impostor: `planes` upright quads crossing at the center (2 = X, 3 = asterisk),
    // each spanning width x height with full [0,1] UVs so the captured clump atlas maps cleanly
    // onto every plane. Base at y=0, centered on X/Z. Sized to fit the clump exactly.
    public static Geometry BuildCrossQuads(int planes, float width, float height)
    {
        planes = Mathf.Max(1, planes);
        width = Mathf.Max(1e-3f, width);
        height = Mathf.Max(1e-3f, height);

        var verts = new Vector3[planes * 4];
        var uvs = new Vector2[planes * 4];
        var idx = new int[planes * 6];
        float half = width * 0.5f;

        for (int k = 0; k < planes; k++)
        {
            float ang = Mathf.PI * k / planes; // spread 0..180 deg
            Vector3 dir = new Vector3(Mathf.Cos(ang), 0.0f, Mathf.Sin(ang));
            int v = k * 4;
            verts[v + 0] = -dir * half;                                   // base left
            verts[v + 1] = dir * half;                                    // base right
            verts[v + 2] = dir * half + new Vector3(0, height, 0);        // top right
            verts[v + 3] = -dir * half + new Vector3(0, height, 0);       // top left
            uvs[v + 0] = new Vector2(0, 0);
            uvs[v + 1] = new Vector2(1, 0);
            uvs[v + 2] = new Vector2(1, 1);
            uvs[v + 3] = new Vector2(0, 1);
            int t = k * 6;
            idx[t + 0] = v + 0; idx[t + 1] = v + 3; idx[t + 2] = v + 2;
            idx[t + 3] = v + 0; idx[t + 4] = v + 2; idx[t + 5] = v + 1;
        }

        return new Geometry { vertices = verts, uvs = uvs, indices = idx, bladeCount = planes, height = height };
    }

    public static Bounds ComputeBounds(Vector3[] v)
    {
        if (v == null || v.Length == 0) return new Bounds(Vector3.zero, Vector3.zero);
        Vector3 mn = v[0], mx = v[0];
        for (int i = 1; i < v.Length; i++) { mn = Vector3.Min(mn, v[i]); mx = Vector3.Max(mx, v[i]); }
        var b = new Bounds();
        b.SetMinMax(mn, mx);
        return b;
    }

    public static Mesh ToMesh(Geometry g, string name = "GrassClump")
    {
        var m = new Mesh { name = name };
        m.indexFormat = g.vertices.Length > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        m.vertices = g.vertices;
        m.uv = g.uvs;
        m.triangles = g.indices;
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }
}
