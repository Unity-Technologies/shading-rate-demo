using UnityEditor;
using UnityEngine;

// Inspector for GrasslandVolume: a "Bake Placement" button (scatters foliage onto the assigned
// terrain from the masks, avoiding the mask-out meshes) and a GPU-culling toggle. Placement is
// baked on-demand (or on enable) rather than on every inspector edit.
[CustomEditor(typeof(GrasslandVolume))]
public class GrasslandVolumeEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var volume = (GrasslandVolume)target;
        EditorGUILayout.Space();

        using (new EditorGUI.DisabledScope(volume.terrainCollider == null))
        {
            if (GUILayout.Button("Bake Placement", GUILayout.Height(28)))
            {
                Undo.RegisterFullObjectHierarchyUndo(volume.gameObject, "Bake Placement");
                volume.BakePlacement();
                EditorUtility.SetDirty(volume);
                SceneView.RepaintAll();
            }
        }

        if (GUILayout.Button($"GPU Culling: {(volume.gpuCulling ? "ON" : "OFF")}  (click to toggle)"))
        {
            Undo.RecordObject(volume, "Toggle GPU Culling");
            volume.gpuCulling = !volume.gpuCulling;
            EditorUtility.SetDirty(volume);
            SceneView.RepaintAll();
        }

        if (GUILayout.Button($"Debug LOD: {(volume.debugLod ? "ON (green=LOD0, red=LOD1)" : "OFF")}  (click to toggle)"))
        {
            Undo.RecordObject(volume, "Toggle Debug LOD");
            volume.debugLod = !volume.debugLod;
            EditorUtility.SetDirty(volume);
            SceneView.RepaintAll();
        }

        if (GUILayout.Button($"Debug HUD (counts): {(volume.debugHud ? "ON" : "OFF")}  (click to toggle)"))
        {
            Undo.RecordObject(volume, "Toggle Debug HUD");
            volume.debugHud = !volume.debugHud;
            EditorUtility.SetDirty(volume);
            SceneView.RepaintAll();
        }

        if (volume.terrainCollider == null)
            EditorGUILayout.HelpBox("Assign a Terrain Collider (the field) to enable placement.", MessageType.Warning);

        // An impostor mesh with no impostor texture draws as solid unclipped quads — easy to miss and
        // easy to fix, so call it out.
        var unclipped = new System.Text.StringBuilder();
        for (int i = 0; i < volume.LayerCount; i++)
        {
            var l = volume.Layer(i);
            if (l == null || l.farMesh == null || l.impostorTexture != null) continue;
            string n = string.IsNullOrEmpty(l.name) ? (i == 0 ? "Grass" : $"Detail {i - 1}") : l.name;
            unclipped.Append(unclipped.Length == 0 ? n : ", " + n);
        }
        if (unclipped.Length > 0)
            EditorGUILayout.HelpBox(
                $"Far mesh but no Impostor Texture on: {unclipped}. Those planes draw as solid " +
                "unclipped quads — assign an RGBA bake (RGB = color, A = coverage).", MessageType.Warning);

        EditorGUILayout.HelpBox(
            "Bake Placement scatters each layer's clumps onto the Terrain Collider, using each " +
            "layer's mask (grayscale = density) and skipping anything over the Mask Out colliders " +
            "(e.g. the road). Re-bake after editing masks / spacing / density. It also re-bakes " +
            "automatically on play.",
            MessageType.Info);
    }
}
