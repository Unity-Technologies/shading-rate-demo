using System.Collections.Generic;
using UnityEngine;

// One painted foliage type. Its mask defines WHERE it spawns (grayscale = density/probability) and
// it supplies two LOD meshes. Instance positions are baked on the CPU from the mask + terrain
// surface into `positions` (world x,y,z + packed yaw/scale).
//
// What a layer's LOD textures mean.
public enum TextureUse
{
    ColorAndCoverage,  // RGB = albedo, A = coverage. Authored foliage whose look lives in its texture.
    CoverageOnly,      // the texture is a mask; color comes from the material's colormap gradient, so
                       // an impostor matches the procedural blades it replaces across the LOD boundary.
}

// How a layer reacts to the deformation trail.
public enum DeformResponse
{
    Flatten,   // pressed toward the ground and darkened, springing back as the trail decays (grass)
    Remove,    // shrinks away and stops being drawn, regrowing as the trail decays (flowers, props)
}

// Each LOD takes its own texture, read according to the layer's textureUse:
//   ColorAndCoverage -> RGB is albedo, ALPHA is coverage. Authored detail foliage.
//   CoverageOnly     -> the texture is a mask; color still comes from the colormap gradient, so a
//                       grass impostor shades identically to the blades it replaces.
//   no texture       -> procedural shading, nothing clipped: the mesh's own silhouette is the
//                       coverage, which is what the grass near LOD wants.
[System.Serializable]
public class FoliageLayer
{
    public string name = "Layer";
    [Tooltip("Draw this layer. Off skips its culling dispatch and both its draws — instant, and it " +
             "keeps the baked placement, so toggling back needs no re-bake.")]
    public bool visible = true;
    [Tooltip("Density mask (grayscale), sampled across the terrain footprint (top-down). Brighter = " +
             "more instances. Empty = this layer spawns nothing (assign a mask to place it).")]
    public Texture2D mask;
    [Tooltip("Shifts the density mask across the terrain footprint, in UV (1.0 = the whole field, so " +
             "0.25 moves it a quarter of the terrain). Lets one painted mask be re-placed per layer " +
             "instead of repainting it. The mask clamps at its edges, so shifting pulls the edge " +
             "pixels across the exposed band. Re-bake placement after changing this.")]
    public Vector2 maskOffset = Vector2.zero;
    [Tooltip("How this layer's LOD textures are read. Color And Coverage = RGB is the color and alpha " +
             "the cutout (authored flowers). Coverage Only = the texture is just a mask and the color " +
             "keeps coming from the colormap gradient — use this for the GRASS impostor so the far LOD " +
             "matches the near blades instead of freezing whatever the bake happened to look like.")]
    public TextureUse textureUse = TextureUse.ColorAndCoverage;
    [Tooltip("Near (dense) LOD mesh. Empty = not drawn.")]
    public Mesh nearMesh;
    [Tooltip("Near LOD texture, read per textureUse. Empty = procedural shading, nothing clipped " +
             "(the usual choice for real grass geometry).")]
    public Texture2D nearTexture;
    [Tooltip("Far (sparse) LOD mesh — typically an X-plane impostor baked in Blender. Empty = not drawn.")]
    public Mesh farMesh;
    [Tooltip("Far LOD impostor texture, read per textureUse. Coverage Only accepts a grayscale mask " +
             "(white-on-black) or an alpha cutout — whichever the texture has is used automatically. " +
             "Empty leaves the impostor planes unclipped (solid quads), so an impostor mesh wants one.")]
    public Texture2D impostorTexture;
    [Range(0.0f, 1.0f)]
    [Tooltip("Coverage cutoff: pixels below this are clipped. ~0.3-0.5 suits a baked alpha; 1 keeps " +
             "only fully opaque texels and erodes badly once mips kick in at distance.")]
    public float alphaCutoff = 0.4f;
    [Tooltip("Candidate spacing in WORLD meters — one clump candidate per cell of this size.")]
    public float spacing = 2.0f;
    [Range(0.0f, 4.0f)]
    [Tooltip("Scales the mask value into a keep-probability (>1 fills masked areas solidly).")]
    public float density = 1.0f;
    [Tooltip("Distance (m) from the camera at which this layer switches near -> far LOD.")]
    public float lodDistance = 35.0f;
    [Range(0.0f, 3.0f)]
    [Tooltip("Per-layer wind sway multiplier (flowers usually sway less than tall grass).")]
    public float windScale = 1.0f;
    [Tooltip("Per-layer clump size: xyz scale applied to this layer's clump geometry.")]
    public Vector3 clumpScale = Vector3.one;
    [Tooltip("Extra scale applied to the FAR (impostor) LOD only, on top of Clump Scale. An impostor " +
             "is a couple of flat planes standing in for a whole clump, so it usually needs widening " +
             "to cover the gaps the geometry used to fill — try x/z around 1.2-1.6 and leave y at 1 to " +
             "keep the horizon line. (1,1,1) = same size as the near LOD.")]
    public Vector3 impostorScale = Vector3.one;
    [Range(0.0f, 1.0f)]
    [Tooltip("Random per-clump height variation (fraction). 0 = all clumps the authored height; " +
             "0.3 = each clump is randomly 0.7x-1.3x tall. Keeps a field from looking uniform.")]
    public float heightVariance = 0.25f;

    [Header("Deformation trail")]
    [Tooltip("What the tractor's trail does to this layer. Flatten = pressed down and darkened, which " +
             "suits grass. Remove = shrinks away and is culled entirely — right for rigid authored " +
             "models like flowers, which look broken folded flat. Both regrow as the trail decays.")]
    public DeformResponse deformResponse = DeformResponse.Flatten;
    [Range(0.05f, 1.0f)]
    [Tooltip("Trail strength at which a Remove layer is culled. Lower values make it disappear sooner " +
             "ahead of the wheel.")]
    public float deformRemoveAt = 0.35f;
    [Tooltip("Only for Remove. On, the model scales down as the trail builds and grows back as it " +
             "decays, so it never pops. Off, it is not deformed at all — full size right up to the " +
             "threshold, then gone. Use Off when any distortion of the model reads as broken.")]
    public bool shrinkOnRemove = true;

    [Header("Look at")]
    [Tooltip("Face a fixed world direction instead of a random yaw — sunflowers all turning to the " +
             "sun. Off = each instance gets a free random yaw (right for grass and most flowers).")]
    public bool lookAt = false;
    [Tooltip("World direction to face: the direction TOWARD the sun, i.e. the negative of your " +
             "directional light's forward. The model's +Z is taken as its facing axis.")]
    public Vector3 lookAtDirection = new Vector3(0.35f, 0.55f, 0.75f);
    [Range(0.0f, 180.0f)]
    [Tooltip("Random per-instance spread around that direction, in degrees. 0 = every instance " +
             "perfectly aligned (reads as artificial); 10-20 keeps the field coherent but natural.")]
    public float lookAtVariance = 12.0f;
    [Range(0.0f, 1.0f)]
    [Tooltip("How much of the direction's ELEVATION becomes tilt. The whole model rotates, so 1 lays " +
             "the stem right over at a high sun; 0 = yaw only (stems stay upright).")]
    public float lookAtTilt = 0.3f;

    // Baked instance positions (world x,y,z + yaw). Runtime-only: regenerated on enable / Bake
    // Placement, not serialized.
    [System.NonSerialized] public Vector4[] positions;
}

// Scene-authored grassland that spawns GPU-indirect foliage onto an EXISTING terrain mesh and
// avoids EXISTING mask-out meshes (e.g. a road). Unlike the original, this does NOT generate any
// terrain — it conforms to the scene's geometry:
//   * `terrainCollider` — the drivable field's collider. Grass is raycast-conformed onto it, and its
//     world bounds define the placement footprint + the deform-trail extent.
//   * `maskOut[]` — colliders (the road, etc.) that veto placement: a candidate whose downward ray
//     hits any of them is skipped.
//   * `repulsors` — moving transforms (the tractor's wheels) that flatten grass into tracks.
// Foliage is one `grass` layer plus N authored `details` layers (see FoliageLayer).
//
// The ProceduralRenderingFeature reads the active volume via the static registry and draws it.
[ExecuteAlways]
public class GrasslandVolume : MonoBehaviour
{
    // The volume the renderer feature draws. Last-enabled wins (typically one per scene).
    public static GrasslandVolume Active { get; private set; }

    [Header("Existing scene geometry")]
    [Tooltip("The field/terrain collider grass conforms to (raycast). Its world bounds set the " +
             "placement footprint and the deform-trail extent. Required.")]
    public Collider terrainCollider;
    [Tooltip("Colliders to AVOID spawning on (e.g. the Road). A candidate whose downward ray hits " +
             "any of these is skipped.")]
    public Collider[] maskOut;
    [Tooltip("Clearance (m) around the mask-out meshes. Positive = keep grass this far BACK from the " +
             "road. 0 = exact road footprint. Negative = let grass ENCROACH up to |value| onto the " +
             "road edge (grass grows a bit into the road).")]
    public float maskOutMargin = 1.0f;
    [Tooltip("Distance (m) over which clumps ramp back up to full size as they get farther from the " +
             "mask-out meshes (the road). Grass near the edge is scaled down so it blends in rather " +
             "than ending in a wall. 0 = no size falloff.")]
    public float maskOutFalloff = 6.0f;
    [Range(0.0f, 1.0f)]
    [Tooltip("Clump size right at the mask-out edge (0 = shrink to nothing, 1 = no shrink).")]
    public float maskOutEdgeScale = 0.2f;

    [Header("Foliage")]
    [Tooltip("The grass. Usually procedural up close (no near texture) and an RGBA impostor bake far " +
             "away.")]
    public FoliageLayer grass = new FoliageLayer { name = "Grass", spacing = 2.0f, density = 1.0f, lodDistance = 45.0f, windScale = 1.0f };

    [Tooltip("Authored detail foliage (flowers etc) — normally textured at both LODs. " +
             "Draw cost ~ (1 + details) x 2.")]
    public FoliageLayer[] details = new FoliageLayer[0];

    [Tooltip("Draw ring (meters) around the camera. Clumps beyond this are culled.")]
    public float cullRadius = 120.0f;
    [Tooltip("Frustum-cull padding (m): clumps within this distance of the view frustum are kept, " +
             "so tall/wide clumps at screen edges don't pop out. Raise if you see edge popping.")]
    public float cullPadding = 5.0f;
    // Toggled via the inspector button; read per-frame by the feature (instant, no rebuild).
    [HideInInspector] public bool gpuCulling = true;
    // Debug: tint clumps by LOD (green = near/LOD0, red = far/LOD1). Instant, read per-frame.
    [HideInInspector] public bool debugLod = false;
    // Debug: on-screen HUD of drawn instance/vertex counts (total + per LOD). Instant, per-frame.
    [HideInInspector] public bool debugHud = false;
    [Tooltip("Wind: xyz = direction, w = strength (0 = no wind). Per-layer windScale multiplies this.")]
    public Vector4 wind = new Vector4(1.0f, -0.2f, 0.3f, 2.5f);
    [Tooltip("Width (m) of the LOD cross-fade band around each layer's lodDistance (screen-door " +
             "dither so the boundary has no hard ring). 0 = hard switch.")]
    public float lodBlend = 12.0f;

    [Header("Placement bake (per-cell jitter)")]
    [Range(0.0f, 1.0f)]
    [Tooltip("Random XZ offset per candidate, as a fraction of the cell spacing (breaks the grid look).")]
    public float positionJitter = 0.35f;
    [Range(0.0f, 1.0f)]
    [Tooltip("Random yaw per clump (fraction of 360 degrees).")]
    public float rotationJitter = 1.0f;

    [Header("Deformation trail")]
    [Tooltip("Transforms that flatten grass as they move. Assign the WHEELS (not the body) to get one " +
             "track per wheel — front and rear on the same side follow the same line, so a four-wheeled " +
             "vehicle leaves two tracks. A single transform gives one wide trail. Max 8; empty = off.")]
    public Transform[] repulsors = new Transform[0];
    [Tooltip("Radius (meters) flattened around EACH repulsor. With wheels assigned this is the track " +
             "half-width, so it wants to be small (~0.3-0.6) — the 3.0 that suited a single body-sized " +
             "stamper will merge the two tracks back into one smear.")]
    public float repulsorRadius = 3.0f;
    [Tooltip("How strongly the trail flattens grass (0 = none, 1 = flat, >1 = flattens sooner).")]
    public float deformStrength = 1.0f;
    [Tooltip("Seconds for the flattened trail to regrow (clear).")]
    public float deformClearTime = 4.0f;
    [Tooltip("Resolution of the persistent trail texture covering the field.")]
    public int deformResolution = 512;

    private const int MaskReadResolution = 1024;

    // ---- Exposed to the render feature -------------------------------------------------------
    public int ConfigVersion { get; private set; }

    // Flat view for the render feature: index 0 is the grass layer, 1.. are the detail layers.
    public int DetailCount => details != null ? details.Length : 0;
    public int LayerCount => 1 + DetailCount;
    public FoliageLayer Layer(int i) => i == 0 ? grass : details[i - 1];

    public Vector4[] LayerPositions(int i) { var l = Layer(i); return l != null ? l.positions : null; }

    // Footprint derived from the terrain collider's world bounds (square extent for the deform RT).
    public Bounds FieldBounds => terrainCollider != null ? terrainCollider.bounds : new Bounds(transform.position, Vector3.one);
    public Vector2 FieldOriginXZ { get { var c = FieldBounds.center; return new Vector2(c.x, c.z); } }
    public float TerrainExtent { get { var s = FieldBounds.size; return Mathf.Max(0.01f, Mathf.Max(s.x, s.z)); } }
    public float WorldExtentX => TerrainExtent;   // square field assumption for the deform mapping

    private void OnEnable()
    {
        Active = this;
        EnsurePlacement();     // [NonSerialized] positions are dropped on domain reload; rebuild if needed
        ConfigVersion++;
    }

    private void OnDisable()
    {
        if (Active == this) Active = null;
    }

    private void Update()
    {
        if (transform.hasChanged)
        {
            transform.hasChanged = false;
            ConfigVersion++;
        }
    }

    // Debug HUD: drawn instance/vertex counts (total + per LOD), populated by the render feature.
    private GUIStyle _hudStyle;
    private void OnGUI()
    {
        if (debugHud) DrawStatsHud();
        if (debugLod) DrawLodLegend();
    }

    // Matches the debug tints in ParticleShader: green = near/LOD0, red = far/LOD1.
    private static readonly Color Lod0Color = new Color(0.1f, 0.85f, 0.1f);
    private static readonly Color Lod1Color = new Color(0.9f, 0.1f, 0.1f);
    private static Texture2D _lodRamp, _lodSolid;
    private GUIStyle _legendStyle;

    // A distance ruler along the bottom of the screen, one bar per layer, showing where that layer
    // switches LOD relative to the cull radius. It answers the question the coloured grass itself
    // can't: whether the boundary you're looking at is where you actually configured it.
    private void DrawLodLegend()
    {
        if (_lodSolid == null)
        {
            _lodSolid = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            _lodSolid.SetPixel(0, 0, Color.white);
            _lodSolid.Apply();
        }
        if (_lodRamp == null)
        {
            // The shader cross-fades with a screen-door dither rather than blending colors, so this
            // ramp stands for "both LODs are drawn here", not for any actual pixel color.
            const int w = 64;
            _lodRamp = new Texture2D(w, 1) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            for (int x = 0; x < w; x++) _lodRamp.SetPixel(x, 0, Color.Lerp(Lod0Color, Lod1Color, (x + 0.5f) / w));
            _lodRamp.Apply();
        }
        if (_legendStyle == null)
        {
            _legendStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, richText = true };
            // Set every state: GUI.skin.label carries its own per-state colors, and assigning only
            // normal leaves hover/active to override it back to the skin's light gray.
            _legendStyle.normal.textColor = Color.black;
            _legendStyle.hover.textColor = Color.black;
            _legendStyle.active.textColor = Color.black;
            _legendStyle.focused.textColor = Color.black;
        }

        const float scale = 2.0f;
        float screenW = Screen.width / scale, screenH = Screen.height / scale;
        const float margin = 14f, barH = 14f, rowH = 34f;
        int rows = LayerCount;
        float x0 = margin + 96f;                       // leave a gutter for the layer name
        float barW = Mathf.Max(40f, screenW - x0 - margin - 46f);
        float y = screenH - margin - rows * rowH;

        Matrix4x4 prev = GUI.matrix;
        Color prevContent = GUI.contentColor;
        GUIUtility.ScaleAroundPivot(new Vector2(scale, scale), Vector2.zero);
        // contentColor tints text specifically, and unlike the style's per-state colors nothing in the
        // skin can override it — the reliable way to force the labels black.
        GUI.contentColor = Color.black;

        for (int i = 0; i < rows; i++, y += rowH)
        {
            var layer = Layer(i);
            if (layer == null) continue;
            float radius = Mathf.Max(0.01f, cullRadius);
            float PxAt(float d) => x0 + Mathf.Clamp01(d / radius) * barW;

            float half = Mathf.Max(0.0f, lodBlend) * 0.5f;
            float nearEnd = Mathf.Clamp(layer.lodDistance - half, 0.0f, radius);
            float farStart = Mathf.Clamp(layer.lodDistance + half, 0.0f, radius);

            // GUI.color multiplies the bar textures; contentColor above keeps the text black either way.
            GUI.color = layer.visible ? Color.white : new Color(1, 1, 1, 0.35f);
            Fill(new Rect(PxAt(0), y, PxAt(nearEnd) - PxAt(0), barH), Lod0Color);
            if (farStart > nearEnd)
                GUI.DrawTexture(new Rect(PxAt(nearEnd), y, PxAt(farStart) - PxAt(nearEnd), barH), _lodRamp);
            Fill(new Rect(PxAt(farStart), y, PxAt(radius) - PxAt(farStart), barH), Lod1Color);
            GUI.color = Color.white;

            string name = string.IsNullOrEmpty(layer.name) ? (i == 0 ? "Grass" : $"Detail {i - 1}") : layer.name;
            if (!layer.visible) name += " <i>(hidden)</i>";
            GUI.Label(new Rect(margin, y - 3f, x0 - margin - 4f, 20f), name, _legendStyle);
            GUI.Label(new Rect(PxAt(radius) + 4f, y - 3f, 60f, 20f), $"{radius:0} m", _legendStyle);
            GUI.Label(new Rect(x0, y + barH, barW, 18f),
                      $"<b>{layer.lodDistance:0.#} m</b> switch" + (lodBlend > 0.01f ? $"  ±{half:0.#} fade" : "  (hard)"),
                      _legendStyle);
        }

        GUI.matrix = prev;
        GUI.contentColor = prevContent;
    }

    private static void Fill(Rect r, Color c)
    {
        if (r.width <= 0.0f) return;
        Color prev = GUI.color;
        GUI.color = new Color(c.r, c.g, c.b, prev.a);
        GUI.DrawTexture(r, _lodSolid);
        GUI.color = prev;
    }

    private void DrawStatsHud()
    {
        if (_hudStyle == null)
            _hudStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 12, richText = true, padding = new RectOffset(10, 10, 8, 8) };

        int i0 = GrasslandStats.Instances[0], i1 = GrasslandStats.Instances[1];
        long v0 = GrasslandStats.Vertices[0], v1 = GrasslandStats.Vertices[1];
        string txt =
            "<b>Grass renderer statistics</b>\n" +
            $"LOD0 (near) : {i0,8:N0} instances   {v0,13:N0} vertices\n" +
            $"LOD1 (far)    : {i1,8:N0} instances   {v1,13:N0} vertices\n" +
            $"Total             : {i0 + i1,8:N0} instances   {v0 + v1,13:N0} vertices";

        // Scale the whole HUD (box AND text) via the GUI matrix — reliably scales the font too,
        // unlike bumping GUIStyle.fontSize. Pivot at the top-left corner so it grows toward the screen.
        const float scale = 3.0f;
        Matrix4x4 prev = GUI.matrix;
        GUIUtility.ScaleAroundPivot(new Vector2(scale, scale), new Vector2(10, 10));
        GUI.Label(new Rect(10, 10, 360, 84), txt, _hudStyle);
        GUI.matrix = prev;
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        cullRadius = Mathf.Max(1.0f, cullRadius);
        cullPadding = Mathf.Max(0.0f, cullPadding);
        lodBlend = Mathf.Max(0.0f, lodBlend);
        maskOutFalloff = Mathf.Max(0.0f, maskOutFalloff);
        maskOutEdgeScale = Mathf.Clamp01(maskOutEdgeScale);
        deformResolution = Mathf.Clamp(deformResolution, 16, 2048);
        for (int i = 0; i < LayerCount; i++)
        {
            var l = Layer(i);
            if (l == null) continue;
            l.spacing = Mathf.Max(0.05f, l.spacing);
            l.lodDistance = Mathf.Clamp(l.lodDistance, 1.0f, cullRadius);
        }
        if (isActiveAndEnabled) ConfigVersion++;
    }
#endif

    // "Bake Placement" button entry point (also called on enable when positions are missing).
    public void BakePlacement()
    {
        if (terrainCollider == null) return;

        Bounds b = terrainCollider.bounds;
        float topY = b.max.y + 10.0f;             // start rays above the terrain
        float castDist = b.size.y + 20.0f;

        for (int i = 0; i < LayerCount; i++)
        {
            var layer = Layer(i);
            if (layer == null) continue;
            layer.positions = ScatterLayer(layer, b, topY, castDist);
        }
        ConfigVersion++;
    }

    private void EnsurePlacement()
    {
        if (terrainCollider == null) return;
        for (int i = 0; i < LayerCount; i++)
        {
            var l = Layer(i);
            if (l != null && l.positions == null) { BakePlacement(); return; }
        }
    }

    // Scatter one layer: jittered candidate grid over the terrain footprint, kept by mask density,
    // conformed to the terrain surface (raycast), and rejected where it hits a mask-out collider.
    private Vector4[] ScatterLayer(FoliageLayer layer, Bounds b, float topY, float castDist)
    {
        // No mask = no placement (keeps an unconfigured layer empty rather than blanketing the field).
        if (layer.mask == null) return System.Array.Empty<Vector4>();

        float spacing = Mathf.Max(0.05f, layer.spacing);
        int nx = Mathf.Max(1, Mathf.CeilToInt(b.size.x / spacing));
        int nz = Mathf.Max(1, Mathf.CeilToInt(b.size.z / spacing));

        Texture2D readable = MakeReadableCopy(layer.mask, MaskReadResolution);

        float jit = Mathf.Clamp01(positionJitter);
        float rot = Mathf.Clamp01(rotationJitter);
        var list = new List<Vector4>();

        for (int j = 0; j < nz; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                uint cell = (uint)(j * nx + i);

                float wx = b.min.x + (i + 0.5f) * spacing + (Hash01(cell * 3u + 1u) * 2f - 1f) * spacing * 0.5f * jit;
                float wz = b.min.z + (j + 0.5f) * spacing + (Hash01(cell * 3u + 2u) * 2f - 1f) * spacing * 0.5f * jit;

                // Mask (top-down over the terrain footprint) as keep-probability, shifted by the
                // layer's maskOffset. The readable copy is clamp-wrapped, so sampling past an edge
                // repeats that edge rather than tiling the mask back around.
                float u = Mathf.InverseLerp(b.min.x, b.max.x, wx) + layer.maskOffset.x;
                float v = Mathf.InverseLerp(b.min.z, b.max.z, wz) + layer.maskOffset.y;
                float m = readable.GetPixelBilinear(u, v).r;
                if (Hash01(cell * 3u + 3u) >= m * layer.density) continue;

                // Conform to the terrain surface. With a negative maskOutMargin (grass encroaching
                // onto the road) fall back to the road surface where there's no terrain under it.
                var ray = new Ray(new Vector3(wx, topY, wz), Vector3.down);
                if (!terrainCollider.Raycast(ray, out RaycastHit hit, castDist))
                {
                    if (maskOutMargin >= 0.0f || !MaskOutClosestHit(ray, castDist, out hit)) continue;
                }

                // Mask-out: reject candidates over the road (or within +margin of it); a negative
                // margin instead lets grass encroach up to |margin| onto the road edge.
                if (IsMaskedOut(wx, wz, topY, castDist)) continue;

                float yaw = Hash01(cell * 7u + 5u) * 6.2831853f * rot;
                float scale = MaskOutProximityScale(wx, wz, topY, castDist);
                list.Add(new Vector4(hit.point.x, hit.point.y, hit.point.z, PackYawScale(yaw, scale)));
            }
        }

        SafeDestroy(ref readable);
        return list.ToArray();
    }

    // Whether a candidate should be rejected for being on/near the mask-out meshes.
    //   margin >= 0 : keep grass BACK — reject if over the road or within +margin of it.
    //   margin <  0 : let grass ENCROACH — keep off-road candidates; for on-road candidates keep
    //                 only those within |margin| of an edge (a probe at that radius is off-road),
    //                 rejecting only the deep road interior.
    private bool IsMaskedOut(float x, float z, float topY, float castDist)
    {
        if (maskOut == null || maskOut.Length == 0) return false;
        bool centerHit = MaskHit(x, z, topY, castDist);

        if (maskOutMargin >= 0.0f)
        {
            if (centerHit) return true;
            float mo = maskOutMargin;
            if (mo > 0.0f)
            {
                if (MaskHit(x + mo, z, topY, castDist)) return true;
                if (MaskHit(x - mo, z, topY, castDist)) return true;
                if (MaskHit(x, z + mo, topY, castDist)) return true;
                if (MaskHit(x, z - mo, topY, castDist)) return true;
            }
            return false;
        }

        // Negative margin (encroach): off-road is always fine; on-road is kept only near an edge.
        if (!centerHit) return false;
        float r = -maskOutMargin;
        if (!MaskHit(x + r, z, topY, castDist)) return false;
        if (!MaskHit(x - r, z, topY, castDist)) return false;
        if (!MaskHit(x, z + r, topY, castDist)) return false;
        if (!MaskHit(x, z - r, topY, castDist)) return false;
        return true;   // surrounded by road out to |margin| => deep interior => reject
    }

    private bool MaskHit(float x, float z, float topY, float castDist)
    {
        var ray = new Ray(new Vector3(x, topY, z), Vector3.down);
        foreach (var c in maskOut)
            if (c != null && c.Raycast(ray, out _, castDist)) return true;
        return false;
    }

    // Closest downward hit among the mask-out colliders (used to conform encroaching grass to the
    // road surface where there's no terrain beneath it).
    private bool MaskOutClosestHit(Ray ray, float castDist, out RaycastHit best)
    {
        best = default;
        float bestDist = float.MaxValue;
        bool any = false;
        if (maskOut != null)
            foreach (var c in maskOut)
                if (c != null && c.Raycast(ray, out RaycastHit h, castDist) && h.distance < bestDist)
                {
                    bestDist = h.distance;
                    best = h;
                    any = true;
                }
        return any;
    }

    // Size multiplier for a clump based on how close it is to the mask-out meshes: probe outward in
    // several directions to find the nearest mask-out hit within maskOutFalloff, then ramp size from
    // maskOutEdgeScale (adjacent to the road) up to 1 (>= falloff away). Keeps grass from ending in a
    // wall at the road. Returns 1 when there is no mask-out geometry or no falloff configured.
    private float MaskOutProximityScale(float x, float z, float topY, float castDist)
    {
        if (maskOut == null || maskOut.Length == 0 || maskOutFalloff <= 0.0001f) return 1.0f;

        const int Dirs = 8, Steps = 4;
        float falloff = maskOutFalloff;
        float nearest = falloff;   // no hit within falloff => full size
        for (int d = 0; d < Dirs; d++)
        {
            float ang = (d / (float)Dirs) * 6.2831853f;
            float dx = Mathf.Cos(ang), dz = Mathf.Sin(ang);
            for (int s = 1; s <= Steps; s++)
            {
                float r = falloff * s / Steps;
                if (MaskHit(x + dx * r, z + dz * r, topY, castDist)) { nearest = Mathf.Min(nearest, r); break; }
            }
        }
        float t = Mathf.SmoothStep(0.0f, 1.0f, Mathf.Clamp01(nearest / falloff));
        return Mathf.Lerp(Mathf.Clamp01(maskOutEdgeScale), 1.0f, t);
    }

    // Pack a clump's size (0..1) and yaw (radians) into one float for the position buffer's .w:
    // integer part = size quantized to 0..1023, fraction = yaw/2pi. The shader unpacks with
    // floor()/frac(). Avoids widening the instance buffer just to carry a per-instance size.
    private static float PackYawScale(float yaw, float scale)
    {
        float scaleQ = Mathf.Floor(Mathf.Clamp01(scale) * 1023.0f);
        // Cap the fraction below 1 with margin so (scaleQ + yaw01) never rounds up into the next
        // integer in float32 (which would corrupt the decoded size).
        float yaw01 = Mathf.Min(Mathf.Repeat(yaw / 6.2831853f, 1.0f), 0.999f);
        return scaleQ + yaw01;
    }

    // Deterministic hash in [0,1) for stable per-cell jitter / rejection.
    private static float Hash01(uint n)
    {
        n ^= 2747636419u; n *= 2654435769u;
        n ^= n >> 16; n *= 2654435769u;
        n ^= n >> 16; n *= 2654435769u;
        return (n & 0xffffffu) / 16777216.0f;
    }

    private static void SafeDestroy(ref Texture2D tex)
    {
        if (tex == null) return;
        if (Application.isPlaying) Destroy(tex);
        else DestroyImmediate(tex);
        tex = null;
    }

    // GPU-blit a (possibly non-readable / compressed) texture into a readable RGBAFloat copy so it
    // can be sampled on the CPU. Caller must destroy the returned texture.
    private static Texture2D MakeReadableCopy(Texture2D src, int size)
    {
        size = Mathf.Max(2, size);
        var prev = RenderTexture.active;
        var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        Graphics.Blit(src, rt);
        RenderTexture.active = rt;

        var tex = new Texture2D(size, size, TextureFormat.RGBAFloat, false, true)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };
        tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
        tex.Apply(false, false);

        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        return tex;
    }
}

// Drawn-this-frame counts published by the render feature (read back from the indirect args
// buffers) and shown by the GrasslandVolume debug HUD. Index 0 = LOD0 (near), 1 = LOD1 (far).
public static class GrasslandStats
{
    public const int Lods = 2;
    public static readonly int[] Instances = new int[Lods];
    public static readonly long[] Vertices = new long[Lods];

    public static void Reset()
    {
        for (int i = 0; i < Lods; i++) { Instances[i] = 0; Vertices[i] = 0; }
    }
}
