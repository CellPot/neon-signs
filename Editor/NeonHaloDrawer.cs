using UnityEditor;
using UnityEngine;

// [NeonHalo] on a float property of Custom/Neon (_Halo): a checkbox that also switches the shader's halo passes
// (LightMode "Always") off or on in the material itself, Material.SetShaderPassEnabled. That list is saved in the .mat
// and so goes into the AssetBundle: TTS needs no script, a sign without halo is simply drawn in one pass instead of
// four. A plain [Toggle] can only set a value or a keyword, the passes would still be drawn (empty).
// Editor only, like the Neon Text window.
public class NeonHaloDrawer : MaterialPropertyDrawer
{
    const string HaloPasses = "Always";

    public override void OnGUI(Rect position, MaterialProperty prop, GUIContent label, MaterialEditor editor)
    {
        EditorGUI.BeginChangeCheck();
        EditorGUI.showMixedValue = prop.hasMixedValue;
        bool on = EditorGUI.Toggle(position, label, prop.floatValue > 0.5f);
        EditorGUI.showMixedValue = false;
        if (!EditorGUI.EndChangeCheck()) return;
        prop.floatValue = on ? 1 : 0;
        foreach (Object t in prop.targets) Sync(t as Material);
    }

    // called when the material is loaded or changed (undo, paste, reset): keeps the passes in step with the value
    public override void Apply(MaterialProperty prop)
    {
        foreach (Object t in prop.targets) Sync(t as Material);
    }

    public static void Sync(Material m)
    {
        if (!m || !m.HasProperty("_Halo")) return;
        bool on = m.GetFloat("_Halo") > 0.5f;
        if (m.GetShaderPassEnabled(HaloPasses) == on) return;
        m.SetShaderPassEnabled(HaloPasses, on);
        EditorUtility.SetDirty(m);
    }
}
