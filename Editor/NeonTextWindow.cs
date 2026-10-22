using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Neon Text window (Tools > Neon Text, or GameObject > Neon Text for a new object): type a text, get round 3D neon
// tubes in the shape of the letters as a plain mesh on the selected object. Nothing is left in the scene but a
// MeshFilter + MeshRenderer, so the object goes into a prefab/AssetBundle as is. Material: whatever the object has;
// if none, Neon.mat from the folder of the last save, or a temporary Custom/Neon one that Save puts next to the mesh
// (as Neon.mat; an untouched temporary one reuses the Neon.mat already there). Nothing is created anywhere else.
// - live preview: on every change the mesh is rebuilt into the object's own unsaved mesh (remembered with its
//   settings, so selecting the object again brings the text back); Save writes it where you choose (Save As dialog the
//   first time, then the same file) and keeps the settings in that mesh's .meta (importer userData).
// - fonts: any TTF/OTF under Assets and in this package; Add... (or a font file dropped on the window) copies one into
//   the Fonts folder of this tool (Assets/NeonSigns/Fonts when the tool is an installed, read-only package).
// - letters: Unity's own font rasteriser (Font.RequestCharactersInTexture; no TextMeshPro resources needed) at Res
//   pixels per em, then the line of the tube:
//   One line (classic neon): Zhang-Suen thinning -> skeleton graph -> short spurs cut, chains joined, free ends
//   straightened (a slanted stroke end bends the skeleton into the corner);
//   Outline (bold fonts): the contour of the signed distance field one tube radius inside the letter (marching
//   squares); a letter whose strokes are thinner than the tube has no room for that and is built as One line.
//   The lines are simplified (RDP), cut into pieces of at most 4 tube radii and rounded (Chaikin) like bent glass.
// - tube: the cross-section (Profile) along the line (letters in the XY plane facing -Z). Round: a glass tube of Sides
//   vertices, ends closed by half-spheres (so joints of the skeleton look welded). Square: a flat-faced LED strip with
//   flat ends; its smooth halo normals go to uv3 (uv4.w = 1). Back Open keeps only the half facing the viewer. uv4 = (letter position along the sign 0..1, 1 = broken letter, 1 = marked)
//   for the per-letter flicker of Custom/Neon (uv4, not uv2: uv2 is the lightmap channel of most meshes).
public class NeonTextWindow : EditorWindow
{
    const int Res = 160;     // raster pixels per em
    const int Pad = 2;       // empty pixels around a glyph bitmap
    const int Sides = 8;     // vertices around the tube (smooth normals hide the facets; 10 was 25 % more for nothing)
    const int CapRings = 2;  // rings of a half-sphere end (one between the tube and the pole)

    public enum Style { OneLine, Outline }
    public enum Shape { Round, Square }
    public enum Back { Full, Open }
    public enum Align { Left, Center, Right }

    [System.Serializable]
    public class Settings
    {
        public string text = "NEON";
        public string fontGuid = "";
        public Style style = Style.OneLine;
        public Shape shape = Shape.Round;  // cross-section: glass tube or flat-faced LED strip
        public Back back = Back.Full;      // Open: only the half facing the viewer (signs on a wall)
        public float size = 1f;            // font size in Unity units (1 em); in TTS 1 unit ~ 1 inch
        public float thickness = 0.06f;    // tube diameter, share of the font size
        public float letterSpacing = 0.05f;
        public float lineSpacing = 0f;
        public Align align = Align.Center;
        public string broken = "";
    }

    [SerializeField] Settings set = new Settings();
    [SerializeField] GameObject target;
    [SerializeField] string savedPath;     // mesh asset of the target, "" = not saved yet
    [SerializeField] bool dirty;           // the target shows a preview that differs from savedPath
    [SerializeField] Mesh preview;         // the target's own unsaved mesh (never shared between objects)

    // An unsaved preview left on an object, with its settings, so selecting that object again brings its text back.
    // One mesh per object: a single shared preview mesh used to be refilled for the next object while the previous
    // one still showed it (the example sign went empty).
    [System.Serializable]
    class Pending
    {
        public Mesh mesh;
        public string settings;   // Settings as JSON
        public string savedPath;
    }
    [SerializeField] List<Pending> pending = new List<Pending>();

    string[] fontGuids;   // fonts under Assets, cached (refreshed when the project changes)
    string status = "";

    [MenuItem("Tools/Neon Text")]
    static void Open() { GetWindow<NeonTextWindow>("Neon Text").OnSelectionChange(); }

    static double lastCreated = -1;

    [MenuItem("GameObject/Neon Text", false, 10)]
    static void CreateObject(MenuCommand cmd)
    {
        // from the Hierarchy menu Unity calls this once per selected object, all in one go: make one sign
        if (EditorApplication.timeSinceStartup - lastCreated < 0.2) return;
        lastCreated = EditorApplication.timeSinceStartup;
        var go = new GameObject("Neon Text");
        GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
        Undo.RegisterCreatedObjectUndo(go, "Create Neon Text");
        var w = GetWindow<NeonTextWindow>("Neon Text");
        w.target = go;
        w.savedPath = "";
        w.preview = null;
        Selection.activeGameObject = go;
        w.Rebuild();
        lastCreated = EditorApplication.timeSinceStartup;   // opening the window and building can take longer than the gap
    }

    void OnSelectionChange()
    {
        var go = Selection.activeGameObject;
        if (!go || go == target) return;
        var mf = go.GetComponent<MeshFilter>();
        var mesh = mf ? mf.sharedMesh : null;
        var p = FindPending(mesh);
        string path = mesh ? AssetDatabase.GetAssetPath(mesh) : "";
        Settings s = path.EndsWith(".asset") ? LoadSettings(path) : null;
        if (p != null)
        {
            // an unsaved preview of this window: its own text and file
            set = JsonUtility.FromJson<Settings>(p.settings);
            savedPath = p.savedPath;
            target = go;
            preview = mesh;
            dirty = true;
        }
        else if (s != null)
        {
            set = s;
            target = go;
            savedPath = path;
            preview = null;
            dirty = false;
        }
        else target = null;        // any other object (even an empty parent): only on request (button)
        status = "";
        Repaint();
    }

    void OnGUI()
    {
        if (!target)
        {
            var sel = Selection.activeGameObject;
            EditorGUILayout.HelpBox("Select a neon text object, make a new one, or put the text on the selected object.", MessageType.Info);
            if (GUILayout.Button("New neon object")) CreateObject(new MenuCommand(null));
            var selMesh = sel ? sel.GetComponent<MeshFilter>() : null;
            if (sel && GUILayout.Button("Put neon text on \"" + sel.name + "\"" + (selMesh && selMesh.sharedMesh ? " (replaces its mesh)" : "")))
            {
                target = sel;
                savedPath = "";
                preview = null;
                Rebuild();
            }
            return;
        }

        EditorGUILayout.LabelField("Object", target.name, EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        HandleFontDrop();
        EditorGUILayout.LabelField("Text");
        set.text = EditorGUILayout.TextArea(set.text, GUILayout.MinHeight(40));
        FontPopup();
        set.style = (Style)EditorGUILayout.EnumPopup(new GUIContent("Style", "One line: classic neon, one tube along the middle of each stroke.\nOutline: the tube runs around each letter (bold fonts)."), set.style);
        set.shape = (Shape)EditorGUILayout.EnumPopup(new GUIContent("Profile", "Round: glass tube.\nSquare: flat-faced LED neon strip with flat ends."), set.shape);
        set.back = (Back)EditorGUILayout.EnumPopup(new GUIContent("Back", "Full: seen from every side.\nOpen: only the half facing the viewer, about half the vertices; for signs on a wall (hollow from behind)."), set.back);
        set.size = Mathf.Max(0.001f, EditorGUILayout.FloatField(new GUIContent("Font size", "1 em in Unity units (in TTS 1 unit is about 1 inch); capital letters are about 0.7 of it."), set.size));
        set.thickness = EditorGUILayout.Slider(new GUIContent("Tube thickness", "Tube diameter, share of the font size."), set.thickness, 0.005f, 0.5f);
        set.letterSpacing = EditorGUILayout.Slider(new GUIContent("Letter spacing", "Extra space between letters, share of the font size."), set.letterSpacing, -0.5f, 3f);
        set.lineSpacing = EditorGUILayout.Slider(new GUIContent("Line spacing", "Extra space between lines, share of the font size."), set.lineSpacing, -1f, 5f);
        set.align = (Align)EditorGUILayout.EnumPopup("Align", set.align);
        set.broken = EditorGUILayout.TextField(new GUIContent("Broken letters", "Letters that flicker on their own: numbers counted over letters only (spaces skipped), 1 = first. E.g. \"7\" or \"2, 7\"."), set.broken);
        if (EditorGUI.EndChangeCheck()) Rebuild();

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(string.IsNullOrEmpty(savedPath) ? "Save..." : "Save")) Save(false);
            if (!string.IsNullOrEmpty(savedPath) && GUILayout.Button("Save As...", GUILayout.Width(90))) Save(true);
        }
        if (string.IsNullOrEmpty(savedPath)) EditorGUILayout.HelpBox("Not saved yet: the mesh lives only in the scene until you save it.", MessageType.Warning);
        else EditorGUILayout.LabelField(dirty ? "Changed, not saved: " + savedPath : "Saved: " + savedPath, EditorStyles.miniLabel);
        if (status.Length > 0) EditorGUILayout.HelpBox(status, MessageType.Warning);
    }

    // Fonts of the project and of this package (see FontGuids). Add... copies a TTF/OTF file into the tool's Fonts folder
    // (see FontsFolder) and selects it; a font file dragged from Explorer (or a font from the Project) onto the window
    // does the same.
    void FontPopup()
    {
        string[] guids = FontGuids();
        if (guids.Length == 0)
        {
            EditorGUILayout.HelpBox("No fonts in the project: add a TTF/OTF.", MessageType.Error);
            if (GUILayout.Button("Add font file...")) AddFont(null);
            return;
        }
        var names = new string[guids.Length];
        for (int i = 0; i < guids.Length; i++) names[i] = Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(guids[i]));
        int index = System.Array.IndexOf(guids, set.fontGuid);
        if (index < 0)
        {
            set.fontGuid = DefaultFontGuid();
            index = System.Array.IndexOf(guids, set.fontGuid);
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            set.fontGuid = guids[EditorGUILayout.Popup("Font", index, names)];
            if (GUILayout.Button(new GUIContent("Add...", "Copy a TTF/OTF file into the Fonts folder of this tool and use it.\nOr drag a font file onto this window."), GUILayout.Width(50)))
                AddFont(null);
        }
    }

    string[] FontGuids()
    {
        if (fontGuids == null)
        {
            // the project's fonts and, when installed as a package, the package's own (Unity's other packages carry UI
            // fonts that are no use here)
            string root = ToolRoot();
            fontGuids = AssetDatabase.FindAssets("t:Font", root.StartsWith("Assets/") ? new[] { "Assets" } : new[] { "Assets", root });
        }
        return fontGuids;
    }

    void OnProjectChange() { fontGuids = null; }

    // Orbitron if it is there, else the first font under Assets ("" if none)
    string DefaultFontGuid()
    {
        string[] guids = FontGuids();
        foreach (string g in guids)
            if (Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(g)) == "Orbitron") return g;
        return guids.Length > 0 ? guids[0] : "";
    }

    // path = a font file anywhere (null = ask); a file inside Assets is used where it is
    void AddFont(string path)
    {
        if (string.IsNullOrEmpty(path))
            path = EditorUtility.OpenFilePanelWithFilters("Add a font", "", new[] { "Fonts", "ttf,otf", "All files", "*" });
        if (string.IsNullOrEmpty(path)) return;
        string full = Path.GetFullPath(path).Replace('\\', '/');
        string assets = Path.GetFullPath(Application.dataPath).Replace('\\', '/');
        string assetPath;
        if (full.StartsWith(assets + "/")) assetPath = "Assets" + full.Substring(assets.Length);
        else
        {
            string fonts = FontsFolder();
            assetPath = AssetDatabase.GenerateUniqueAssetPath(fonts + "/" + Path.GetFileName(full));
            File.Copy(full, assetPath);
            AssetDatabase.ImportAsset(assetPath);
            fontGuids = null;
        }
        if (!AssetDatabase.LoadAssetAtPath<Font>(assetPath)) { status = Path.GetFileName(full) + " is not a font Unity can read."; return; }
        set.fontGuid = AssetDatabase.AssetPathToGUID(assetPath);
        GUI.changed = true;
    }

    // The folder of this tool, above the Editor folder of this script: Assets/NeonSigns when copied into a project,
    // Packages/com.cellpot.neon-signs when installed with the Package Manager
    string ToolRoot()
    {
        string script = AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(this));
        return Path.GetDirectoryName(Path.GetDirectoryName(script)).Replace('\\', '/');
    }

    // Where Add... puts fonts: <tool>/Fonts inside Assets; an installed package is read-only, so then Assets/NeonSigns/Fonts.
    // Made if missing.
    string FontsFolder()
    {
        string root = ToolRoot();
        if (!root.StartsWith("Assets/"))
        {
            if (!AssetDatabase.IsValidFolder("Assets/NeonSigns")) AssetDatabase.CreateFolder("Assets", "NeonSigns");
            root = "Assets/NeonSigns";
        }
        if (!AssetDatabase.IsValidFolder(root + "/Fonts")) AssetDatabase.CreateFolder(root, "Fonts");
        return root + "/Fonts";
    }

    static bool IsFontFile(string p)
    {
        string ext = Path.GetExtension(p).ToLowerInvariant();
        return ext == ".ttf" || ext == ".otf";
    }

    // a font file from Explorer or a font from the Project dropped anywhere on the window
    void HandleFontDrop()
    {
        var e = Event.current;
        if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform) return;
        string file = System.Array.Find(DragAndDrop.paths ?? new string[0], IsFontFile);
        Font asset = null;
        foreach (var o in DragAndDrop.objectReferences) if (o is Font f) asset = f;
        if (file == null && !asset) return;
        DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
        if (e.type == EventType.DragPerform)
        {
            DragAndDrop.AcceptDrag();
            if (asset) { set.fontGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset)); GUI.changed = true; }
            else AddFont(file);
        }
        e.Use();
    }

    void Rebuild()
    {
        if (!target) return;
        var font = AssetDatabase.LoadAssetAtPath<Font>(AssetDatabase.GUIDToAssetPath(set.fontGuid));
        if (!font)
        {
            set.fontGuid = DefaultFontGuid();
            font = AssetDatabase.LoadAssetAtPath<Font>(AssetDatabase.GUIDToAssetPath(set.fontGuid));
            if (!font) { status = "No fonts in the project: add a TTF/OTF."; return; }
        }
        // build into a scratch mesh first: if it fails, the object keeps what it had
        var built = new Mesh();
        try { status = Build(set, font, built); }
        catch (System.Exception e)
        {
            DestroyImmediate(built);
            status = "Could not build this text: " + e.Message;
            Debug.LogException(e);
            return;
        }

        var mf = target.GetComponent<MeshFilter>();
        if (!mf) mf = Undo.AddComponent<MeshFilter>(target);
        if (!preview || mf.sharedMesh != preview) preview = new Mesh();   // only ever refill the target's own preview
        preview.name = "Neon Text (unsaved)";
        CopyMesh(built, preview);
        DestroyImmediate(built);
        var p = FindPending(preview);
        if (p == null) pending.Add(p = new Pending { mesh = preview });
        p.settings = JsonUtility.ToJson(set);
        p.savedPath = savedPath;
        var mr = target.GetComponent<MeshRenderer>();
        if (!mr)
        {
            mr = Undo.AddComponent<MeshRenderer>(target);
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }
        if (!mr.sharedMaterial)
        {
            mr.sharedMaterial = PreviewMaterial();
            if (!mr.sharedMaterial)
                status = (status.Length > 0 ? status + "\n" : "") + "The Custom/Neon shader is not in the project (NeonSigns/Shaders/Neon.shader): the text has no material.";
        }
        Undo.RecordObject(mf, "Neon Text");
        mf.sharedMesh = preview;
        dirty = true;
        SceneView.RepaintAll();
    }

    void Save(bool saveAs)
    {
        var src = target ? target.GetComponent<MeshFilter>() : null;
        if (!src || !src.sharedMesh) return;
        string path = savedPath;
        if (saveAs || string.IsNullOrEmpty(path))
        {
            string folder = EditorPrefs.GetString("NeonText.Folder", "Assets");
            string name = MakeName(set.text);
            path = EditorUtility.SaveFilePanelInProject("Save neon text mesh", name, "asset", "Where to save the mesh", folder);
            if (string.IsNullOrEmpty(path)) return;
            EditorPrefs.SetString("NeonText.Folder", Path.GetDirectoryName(path).Replace('\\', '/'));
        }
        var from = src.sharedMesh;   // the preview, or the saved mesh itself when nothing changed
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (mesh)
        {
            if (mesh != from) CopyMesh(from, mesh);   // same file, same GUID: prefabs that use it update too
            EditorUtility.SetDirty(mesh);
        }
        else
        {
            mesh = new Mesh();
            CopyMesh(from, mesh);
            AssetDatabase.CreateAsset(mesh, path);
        }
        mesh.name = Path.GetFileNameWithoutExtension(path);
        AssetDatabase.SaveAssetIfDirty(mesh);
        var importer = AssetImporter.GetAtPath(path);
        importer.userData = JsonUtility.ToJson(set);
        importer.SaveAndReimport();

        var mf = target.GetComponent<MeshFilter>();
        Undo.RecordObject(mf, "Save Neon Text");
        mf.sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);

        // a material that is not an asset yet (the temporary preview one) goes next to the mesh: the Neon.mat already
        // there if the temporary one was not touched, else saved as Neon.mat (Neon 1.mat, ... if that name is taken),
        // so changes made to it before saving are kept
        var mr = target.GetComponent<MeshRenderer>();
        if (mr && mr.sharedMaterial && !AssetDatabase.Contains(mr.sharedMaterial))
        {
            var temp = mr.sharedMaterial;
            string matPath = Path.GetDirectoryName(path).Replace('\\', '/') + "/Neon.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (!mat || !IsUntouched(temp))
            {
                mat = new Material(temp) { name = "Neon" };
                NeonHaloDrawer.Sync(mat);   // halo passes off if the Halo box was cleared on the temporary material
                AssetDatabase.CreateAsset(mat, AssetDatabase.GenerateUniqueAssetPath(matPath));
            }
            Undo.RecordObject(mr, "Save Neon Text");
            mr.sharedMaterial = mat;
        }
        savedPath = path;
        dirty = false;
        pending.Remove(FindPending(preview));
        preview = null;
    }

    // the record of an unsaved preview mesh (null if none); forgets the ones whose mesh is gone
    Pending FindPending(Mesh mesh)
    {
        pending.RemoveAll(x => x == null || !x.mesh);
        return mesh ? pending.Find(x => x.mesh == mesh) : null;
    }

    static Settings LoadSettings(string path)
    {
        var importer = AssetImporter.GetAtPath(path);
        if (!importer || string.IsNullOrEmpty(importer.userData) || !importer.userData.Contains("\"thickness\"")) return null;
        try { return JsonUtility.FromJson<Settings>(importer.userData); }
        catch { return null; }
    }

    // Neon.mat from the folder of the last save, if there is one; else a temporary Custom/Neon material (saved by Save);
    // null if the shader is missing from the project
    static Material PreviewMaterial()
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(EditorPrefs.GetString("NeonText.Folder", "Assets") + "/Neon.mat");
        if (mat) return mat;
        var shader = Shader.Find("Custom/Neon");
        return shader ? new Material(shader) { name = "Neon" } : null;
    }

    // true if every property of the material still has the shader's default value
    static bool IsUntouched(Material m)
    {
        var fresh = new Material(m.shader);
        bool same = m.mainTextureOffset == fresh.mainTextureOffset && m.mainTextureScale == fresh.mainTextureScale;
        for (int i = 0; same && i < m.shader.GetPropertyCount(); i++)
        {
            string n = m.shader.GetPropertyName(i);
            switch (m.shader.GetPropertyType(i))
            {
                case ShaderPropertyType.Color:
                case ShaderPropertyType.Vector: same = m.GetVector(n) == fresh.GetVector(n); break;
                case ShaderPropertyType.Texture: same = m.GetTexture(n) == fresh.GetTexture(n); break;
                case ShaderPropertyType.Int: same = m.GetInteger(n) == fresh.GetInteger(n); break;
                default: same = Mathf.Approximately(m.GetFloat(n), fresh.GetFloat(n)); break;
            }
        }
        DestroyImmediate(fresh);
        return same;
    }

    static string MakeName(string text)
    {
        var sb = new System.Text.StringBuilder("Neon_");
        foreach (char c in text.Trim())
            sb.Append(c < 128 && char.IsLetterOrDigit(c) ? c : '_');
        string r = sb.ToString().TrimEnd('_');
        return r.Length > 40 ? r.Substring(0, 40) : r;
    }

    static void CopyMesh(Mesh from, Mesh to)
    {
        to.Clear();
        to.indexFormat = from.indexFormat;
        to.vertices = from.vertices;
        to.normals = from.normals;
        var l = new List<Vector4>();
        from.GetUVs(3, l);
        to.SetUVs(3, l);
        var h = new List<Vector3>();
        from.GetUVs(2, h);
        if (h.Count > 0) to.SetUVs(2, h);
        to.triangles = from.triangles;
        to.RecalculateBounds();
    }

    // ---------------------------------------------------------------- build

    class Glyph
    {
        public CharacterInfo ci;
        public List<List<Vector2>> lines = new List<List<Vector2>>();   // raster px from the pen/baseline
        public List<bool> closed = new List<bool>();
        public bool oneLine;   // Outline asked, but the strokes are thinner than the tube: built as One line
    }

    // Builds the tubes of the text into mesh; returns a warning ("" = fine).
    public static string Build(Settings s, Font font, Mesh mesh)
    {
        string text = (s.text ?? "").Replace("\r", "");
        mesh.Clear();
        if (!font.dynamic) return "The font must be imported as Dynamic (the default).";
        if (text.Trim().Length == 0) return "";

        float rPx = s.thickness * Res / 2;   // tube radius in raster px
        font.RequestCharactersInTexture(text.Replace("\n", ""), Res);
        Color32[] fpx = ReadFontTexture(font, out int fw, out int fh);

        var glyphs = new Dictionary<char, Glyph>();
        string missing = "", thin = "";
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || glyphs.ContainsKey(c) || missing.IndexOf(c) >= 0) continue;
            if (!font.GetCharacterInfo(c, out CharacterInfo ci, Res) || ci.glyphWidth <= 0 || ci.glyphHeight <= 0)
            {
                missing += c;
                continue;
            }
            var g = MakeGlyph(ci, fpx, fw, fh, s.style, s.shape == Shape.Square, rPx);
            glyphs[c] = g;
            if (g.oneLine) thin += c;
        }
        var warns = new List<string>();
        if (missing.Length > 0) warns.Add("No \"" + missing + "\" in " + font.name + ".");
        if (thin.Length > 0) warns.Add("Outline: the strokes of \"" + thin + "\" are thinner than the tube, so they are built as One line (or make the tube thinner).");
        string warn = string.Join("\n", warns);

        var broken = new HashSet<int>();
        foreach (string part in System.Text.RegularExpressions.Regex.Split(s.broken ?? "", @"\D+"))
            if (int.TryParse(part, out int b)) broken.Add(b - 1);

        // layout in raster px: pen positions, baseline of line i at -i * lineH
        float lineH = font.lineHeight * (float)Res / font.fontSize + s.lineSpacing * Res;
        string[] lines = text.Split('\n');
        var placed = new List<(Glyph g, float x, float y, int line)>();
        var lineW = new float[lines.Length];
        for (int li = 0; li < lines.Length; li++)
        {
            float pen = 0, right = 0;
            foreach (char c in lines[li])
            {
                CharacterInfo ci;
                if (glyphs.TryGetValue(c, out Glyph g)) { placed.Add((g, pen, -li * lineH, li)); ci = g.ci; }
                else if (!font.GetCharacterInfo(c, out ci, Res)) continue;
                right = pen + ci.advance;
                pen += ci.advance + s.letterSpacing * Res;
            }
            lineW[li] = right;
        }
        if (placed.Count == 0) return warn;

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        var offsets = new List<Vector2>();
        foreach (var p in placed)
        {
            float off = s.align == Align.Left ? 0 : s.align == Align.Center ? -lineW[p.line] / 2 : -lineW[p.line];
            var o = new Vector2(p.x + off, p.y);
            offsets.Add(o);
            minX = Mathf.Min(minX, o.x + p.g.ci.minX);
            maxX = Mathf.Max(maxX, o.x + p.g.ci.maxX);
            minY = Mathf.Min(minY, o.y + p.g.ci.minY);
            maxY = Mathf.Max(maxY, o.y + p.g.ci.maxY);
        }
        var centre = new Vector2((minX + maxX) / 2, (minY + maxY) / 2);   // the ink is centred on the object
        float m = s.size / Res;

        var tube = new TubeMesh(MakeProfile(s.shape, s.back));
        int count = placed.Count;
        for (int i = 0; i < count; i++)
        {
            var g = placed[i].g;
            // uv4: position along the sign, broken, "letters marked", "uv3 holds halo normals" (sharp profiles)
            var l = new Vector4(count > 1 ? (float)i / (count - 1) : 0, broken.Contains(i) ? 1 : 0, 1, tube.Sharp ? 1 : 0);
            for (int k = 0; k < g.lines.Count; k++)
            {
                var pts = new List<Vector2>(g.lines[k].Count);
                foreach (var p in g.lines[k]) pts.Add((p + offsets[i] - centre) * m);
                tube.Add(pts, g.closed[k], rPx * m, l);
            }
        }
        mesh.indexFormat = tube.verts.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.SetVertices(tube.verts);
        mesh.SetNormals(tube.normals);
        if (tube.Sharp) mesh.SetUVs(2, tube.haloNormals);
        mesh.SetUVs(3, tube.letter);
        mesh.SetTriangles(tube.tris, 0);
        mesh.RecalculateBounds();
        return warn;
    }

    static Color32[] ReadFontTexture(Font font, out int w, out int h)
    {
        Texture src = font.material.mainTexture;
        w = src.width;
        h = src.height;
        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        var prev = RenderTexture.active;   // before Blit: Blit makes rt the active one
        Graphics.Blit(src, rt);
        RenderTexture.active = rt;
        var tmp = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
        tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tmp.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        Color32[] px = tmp.GetPixels32();
        DestroyImmediate(tmp);
        return px;
    }

    // sharp: straight runs with corners (Square profile) instead of lines rounded like bent glass
    static Glyph MakeGlyph(CharacterInfo ci, Color32[] fpx, int fw, int fh, Style style, bool sharp, float rPx)
    {
        int gw = ci.glyphWidth, gh = ci.glyphHeight;
        int W = gw + 2 * Pad, H = gh + 2 * Pad;
        var ink = new bool[W * H];
        for (int y = 0; y < gh; y++)
            for (int x = 0; x < gw; x++)
            {
                float u = (x + 0.5f) / gw, v = (y + 0.5f) / gh;   // the glyph may be stored flipped: go by its corners
                Vector2 uv = ci.uvBottomLeft + (ci.uvBottomRight - ci.uvBottomLeft) * u + (ci.uvTopLeft - ci.uvBottomLeft) * v;
                int px = Mathf.Clamp((int)(uv.x * fw), 0, fw - 1), py = Mathf.Clamp((int)(uv.y * fh), 0, fh - 1);
                ink[(y + Pad) * W + x + Pad] = fpx[py * fw + px].a > 127;
            }
        float[] toOut = Edt(ink, W, H, false);   // squared distance of ink pixels to the nearest empty one
        var skeleton = (bool[])ink.Clone();
        Thin(skeleton, W, H);

        var raw = new List<List<Vector2>>();
        var rawClosed = new List<bool>();
        // Outline puts the tube one radius inside the edge, so a stroke thinner than the tube has no room for it: thin
        // fonts came out empty, letters thin only in places came out as broken bits of outline. A letter with a fifth
        // or more of its strokes too thin is built as One line instead.
        bool oneLine = style == Style.OneLine || ThinShare(skeleton, toOut, rPx + 1) >= 0.2f;
        if (!oneLine)
        {
            float[] toIn = Edt(ink, W, H, true);
            var sd = new float[W * H];   // signed distance at pixel centres, + inside
            for (int p = 0; p < sd.Length; p++) sd[p] = ink[p] ? Mathf.Sqrt(toOut[p]) - 0.5f : 0.5f - Mathf.Sqrt(toIn[p]);
            Contours(sd, W, H, rPx, raw, rawClosed);
            if (raw.Count == 0) oneLine = true;
        }
        if (oneLine) SkeletonLines(skeleton, toOut, W, H, raw, rawClosed);

        var g = new Glyph { ci = ci, oneLine = oneLine && style == Style.Outline };
        var origin = new Vector2(ci.minX - Pad + 0.5f, ci.minY - Pad + 0.5f);   // pixel (0,0) centre from the pen/baseline
        for (int i = 0; i < raw.Count; i++)
        {
            var pts = sharp ? Rdp(raw[i], rawClosed[i], Mathf.Max(1, 0.5f * rPx)) : Smooth(raw[i], rawClosed[i], rPx);
            if (pts.Count < 2) continue;
            // Outline: where a stroke is only a little wider than the tube, the contour falls apart into specks a few
            // pixels long; a tube round such a speck folds into itself (dark shards on the Square profile)
            if (rawClosed[i] && (pts.Count < 3 || Length(pts) < 6 * rPx)) continue;
            for (int k = 0; k < pts.Count; k++) pts[k] += origin;
            g.lines.Add(pts);
            g.closed.Add(rawClosed[i]);
        }
        return g;
    }

    // ---------------------------------------------------------------- one line: skeleton

    static void Thin(bool[] img, int W, int H)
    {
        var del = new List<int>();
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int step = 0; step < 2; step++)
            {
                del.Clear();
                for (int y = 1; y < H - 1; y++)
                    for (int x = 1; x < W - 1; x++)
                    {
                        int i = y * W + x;
                        if (!img[i]) continue;
                        bool p2 = img[i + W], p3 = img[i + W + 1], p4 = img[i + 1], p5 = img[i - W + 1];
                        bool p6 = img[i - W], p7 = img[i - W - 1], p8 = img[i - 1], p9 = img[i + W - 1];
                        int b = (p2 ? 1 : 0) + (p3 ? 1 : 0) + (p4 ? 1 : 0) + (p5 ? 1 : 0) + (p6 ? 1 : 0) + (p7 ? 1 : 0) + (p8 ? 1 : 0) + (p9 ? 1 : 0);
                        if (b < 2 || b > 6) continue;
                        int a = (!p2 && p3 ? 1 : 0) + (!p3 && p4 ? 1 : 0) + (!p4 && p5 ? 1 : 0) + (!p5 && p6 ? 1 : 0) +
                                (!p6 && p7 ? 1 : 0) + (!p7 && p8 ? 1 : 0) + (!p8 && p9 ? 1 : 0) + (!p9 && p2 ? 1 : 0);
                        if (a != 1) continue;
                        if (step == 0 ? (p2 && p4 && p6) || (p4 && p6 && p8) : (p2 && p4 && p8) || (p2 && p6 && p8)) continue;
                        del.Add(i);
                    }
                foreach (int i in del) img[i] = false;
                if (del.Count > 0) changed = true;
            }
        }
    }

    // Neighbours of a skeleton pixel: the 4 straight ones, plus a diagonal one only when no straight pixel joins them
    // already (otherwise every staircase step would look like a junction). The glyph border is empty (Pad), so i +- W
    // +- 1 stays inside.
    static int Neighbours(bool[] sk, int W, int i, int[] nb)
    {
        int n = 0;
        bool up = sk[i + W], right = sk[i + 1], down = sk[i - W], left = sk[i - 1];
        if (up) nb[n++] = i + W;
        if (right) nb[n++] = i + 1;
        if (down) nb[n++] = i - W;
        if (left) nb[n++] = i - 1;
        if (sk[i + W + 1] && !up && !right) nb[n++] = i + W + 1;
        if (sk[i - W + 1] && !down && !right) nb[n++] = i - W + 1;
        if (sk[i - W - 1] && !down && !left) nb[n++] = i - W - 1;
        if (sk[i + W - 1] && !up && !left) nb[n++] = i + W - 1;
        return n;
    }

    class Edge { public int a, b; public List<Vector2> pts; public bool closed; }

    // lines of the tube from a thinned glyph (sk): skeleton graph -> spurs cut -> chains joined -> free ends straightened
    static void SkeletonLines(bool[] sk, float[] toOut, int W, int H, List<List<Vector2>> outLines, List<bool> outClosed)
    {
        int N = W * H;
        var deg = new int[N];
        var nb = new int[8];
        for (int i = 0; i < N; i++) if (sk[i]) deg[i] = Neighbours(sk, W, i, nb);

        // nodes = pixels with other than 2 neighbours; touching node pixels form one node (cluster)
        var cluster = new int[N];
        for (int i = 0; i < N; i++) cluster[i] = -1;
        var centres = new List<Vector2>();
        var halfWidth = new List<float>();
        var queue = new Stack<int>();
        for (int i = 0; i < N; i++)
        {
            if (!sk[i] || deg[i] == 2 || cluster[i] >= 0) continue;
            int id = centres.Count;
            Vector2 sum = Vector2.zero;
            float hw = 0;
            int cnt = 0;
            queue.Push(i);
            cluster[i] = id;
            while (queue.Count > 0)
            {
                int p = queue.Pop();
                sum += new Vector2(p % W, p / W);
                hw = Mathf.Max(hw, Mathf.Sqrt(toOut[p]));
                cnt++;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int q = p + dy * W + dx;
                        if (sk[q] && deg[q] != 2 && cluster[q] < 0) { cluster[q] = id; queue.Push(q); }
                    }
            }
            centres.Add(sum / cnt);
            halfWidth.Add(hw);
        }

        // edges between nodes along the 2-neighbour pixels
        var edges = new List<Edge>();
        var visited = new bool[N];
        var nb2 = new int[8];
        for (int i = 0; i < N; i++)
        {
            if (cluster[i] < 0) continue;
            int n = Neighbours(sk, W, i, nb);
            for (int k = 0; k < n; k++)
            {
                int q = nb[k];
                if (cluster[q] >= 0 || visited[q]) continue;
                var pts = new List<Vector2> { centres[cluster[i]] };
                int prev = i, cur = q, end = -1;
                while (true)
                {
                    visited[cur] = true;
                    pts.Add(new Vector2(cur % W, cur / W));
                    int m = Neighbours(sk, W, cur, nb2), next = -1;
                    for (int j = 0; j < m; j++) if (nb2[j] != prev && !(cluster[nb2[j]] < 0 && visited[nb2[j]])) { next = nb2[j]; break; }
                    if (next < 0) break;
                    if (cluster[next] >= 0) { end = cluster[next]; pts.Add(centres[end]); break; }
                    prev = cur;
                    cur = next;
                }
                edges.Add(new Edge { a = cluster[i], b = end, pts = pts });
            }
        }
        // loops without any node (O, 0)
        for (int i = 0; i < N; i++)
        {
            if (!sk[i] || cluster[i] >= 0 || visited[i]) continue;
            var pts = new List<Vector2>();
            int prev = -1, cur = i;
            while (cur >= 0 && !visited[cur])
            {
                visited[cur] = true;
                pts.Add(new Vector2(cur % W, cur / W));
                int m = Neighbours(sk, W, cur, nb2), next = -1;
                for (int j = 0; j < m; j++) if (nb2[j] != prev && !visited[nb2[j]]) { next = nb2[j]; break; }
                prev = cur;
                cur = next;
            }
            if (pts.Count >= 6) edges.Add(new Edge { a = -1, b = -1, pts = pts, closed = true });
        }

        // tiny cycles inside a node, then spurs: short dead ends off a junction (thinning noise at corners)
        edges.RemoveAll(e => !e.closed && e.a == e.b && e.a >= 0 && Length(e.pts) < 2 * halfWidth[e.a] + 4);
        for (int pass = 0; pass < 3; pass++)
        {
            var ends = CountEnds(edges, centres.Count);
            int removed = edges.RemoveAll(e =>
            {
                if (e.closed) return false;
                int tip = e.b < 0 || ends[e.b] == 1 ? e.b : e.a >= 0 && ends[e.a] == 1 ? e.a : -2;
                int root = tip == e.b ? e.a : e.b;
                if (tip == -2 || root < 0 || ends[root] < 3) return false;
                return Length(e.pts) < 1.6f * halfWidth[root] + 2;
            });
            if (removed == 0) break;
        }

        // join chains through nodes with exactly two ends, so a stroke is one tube
        bool joined = true;
        while (joined)
        {
            joined = false;
            var ends = CountEnds(edges, centres.Count);
            for (int c = 0; c < centres.Count && !joined; c++)
            {
                if (ends[c] != 2) continue;
                int e1 = edges.FindIndex(e => !e.closed && (e.a == c || e.b == c));
                if (e1 < 0) continue;
                var A = edges[e1];
                if (A.a == c && A.b == c) { A.closed = true; A.a = A.b = -1; joined = true; break; }
                int e2 = edges.FindIndex(e => e != A && !e.closed && (e.a == c || e.b == c));
                if (e2 < 0) continue;
                var B = edges[e2];
                if (A.a == c) { A.pts.Reverse(); (A.a, A.b) = (A.b, A.a); }   // A ends at c
                if (B.b == c) { B.pts.Reverse(); (B.a, B.b) = (B.b, B.a); }   // B starts at c
                A.pts.AddRange(B.pts.GetRange(1, B.pts.Count - 1));
                A.b = B.b;
                edges.RemoveAt(e2);
                joined = true;
            }
        }

        // straighten free ends: at a stroke end cut at a slant the skeleton bends into the corner (hooks on italics,
        // on the legs of A and R)
        var tips = CountEnds(edges, centres.Count);
        foreach (var e in edges)
        {
            if (e.closed) continue;
            if (e.b < 0 || tips[e.b] == 1) StraightenEnd(e.pts, toOut, W, H);
            if (e.a < 0 || tips[e.a] == 1) { e.pts.Reverse(); StraightenEnd(e.pts, toOut, W, H); e.pts.Reverse(); }
        }

        foreach (var e in edges)
        {
            if (e.pts.Count < 2) continue;
            if (e.closed && e.pts.Count > 2 && (e.pts[0] - e.pts[e.pts.Count - 1]).sqrMagnitude < 0.01f) e.pts.RemoveAt(e.pts.Count - 1);
            outLines.Add(e.pts);
            outClosed.Add(e.closed);
        }
    }

    // The last 2 stroke half-widths of the line (its end is the last point) become a straight run in the direction
    // the stroke had before them, reaching as far along it as the bent end did. Short strokes are left alone.
    static void StraightenEnd(List<Vector2> p, float[] toOut, int W, int H)
    {
        var hw = new List<float>();
        foreach (var q in p)
        {
            int x = Mathf.Clamp(Mathf.RoundToInt(q.x), 0, W - 1), y = Mathf.Clamp(Mathf.RoundToInt(q.y), 0, H - 1);
            hw.Add(Mathf.Sqrt(toOut[y * W + x]));
        }
        hw.Sort();
        float half = hw[hw.Count / 2];
        float trim = 2f * half;
        if (half <= 0 || Length(p) < trim + 2.5f * half) return;   // what is left must still show the direction
        int k = p.Count - 1;
        for (float acc = 0; k > 0 && acc < trim; k--) acc += Vector2.Distance(p[k], p[k - 1]);
        int j = k;
        for (float acc = 0; j > 0 && acc < 2 * half; j--) acc += Vector2.Distance(p[j], p[j - 1]);
        Vector2 dir = (p[k] - p[j]).normalized;
        float reach = Mathf.Max(0, Vector2.Dot(p[p.Count - 1] - p[k], dir));
        p.RemoveRange(k + 1, p.Count - k - 1);
        if (reach > 0.5f) p.Add(p[k] + dir * reach);
    }

    // share of the skeleton sk where the stroke's half-width (inside distance) is below `half`, raster px; 1 if empty
    static float ThinShare(bool[] sk, float[] toOut, float half)
    {
        int all = 0, thin = 0;
        for (int i = 0; i < sk.Length; i++)
        {
            if (!sk[i]) continue;
            all++;
            if (toOut[i] < half * half) thin++;
        }
        return all == 0 ? 1 : (float)thin / all;
    }

    static int[] CountEnds(List<Edge> edges, int nodes)
    {
        var ends = new int[nodes];
        foreach (var e in edges)
        {
            if (e.closed) continue;
            if (e.a >= 0) ends[e.a]++;
            if (e.b >= 0) ends[e.b]++;
        }
        return ends;
    }

    static float Length(List<Vector2> pts)
    {
        float l = 0;
        for (int i = 1; i < pts.Count; i++) l += Vector2.Distance(pts[i - 1], pts[i]);
        return l;
    }

    // ---------------------------------------------------------------- outline: contour of the distance field

    static void Contours(float[] f, int W, int H, float level, List<List<Vector2>> outLines, List<bool> outClosed)
    {
        // segments of the iso-line in the cells between pixel centres; edge ids: 2*(y*W+x) horizontal, +1 vertical
        var links = new Dictionary<int, List<int>>();
        var pos = new Dictionary<int, Vector2>();
        void Link(int e1, int e2)
        {
            if (!links.TryGetValue(e1, out var l1)) links[e1] = l1 = new List<int>();
            if (!links.TryGetValue(e2, out var l2)) links[e2] = l2 = new List<int>();
            l1.Add(e2);
            l2.Add(e1);
        }
        int EdgeId(int x, int y, bool vertical)
        {
            int id = 2 * (y * W + x) + (vertical ? 1 : 0);
            if (!pos.ContainsKey(id))
            {
                float a = f[y * W + x], b = vertical ? f[(y + 1) * W + x] : f[y * W + x + 1];
                float t = Mathf.Clamp01((level - a) / (b - a));
                pos[id] = vertical ? new Vector2(x, y + t) : new Vector2(x + t, y);
            }
            return id;
        }
        for (int y = 0; y < H - 1; y++)
            for (int x = 0; x < W - 1; x++)
            {
                bool c0 = f[y * W + x] > level, c1 = f[y * W + x + 1] > level, c2 = f[(y + 1) * W + x + 1] > level, c3 = f[(y + 1) * W + x] > level;
                int code = (c0 ? 1 : 0) | (c1 ? 2 : 0) | (c2 ? 4 : 0) | (c3 ? 8 : 0);
                if (code == 0 || code == 15) continue;
                int bottom = -1, right = -1, top = -1, left = -1;
                if (c0 != c1) bottom = EdgeId(x, y, false);
                if (c1 != c2) right = EdgeId(x + 1, y, true);
                if (c3 != c2) top = EdgeId(x, y + 1, false);
                if (c0 != c3) left = EdgeId(x, y, true);
                if (code == 5 || code == 10)
                {
                    bool centreIn = (f[y * W + x] + f[y * W + x + 1] + f[(y + 1) * W + x + 1] + f[(y + 1) * W + x]) / 4 > level;
                    if ((code == 5) == centreIn) { Link(bottom, right); Link(top, left); }
                    else { Link(bottom, left); Link(top, right); }
                }
                else
                {
                    var e = new List<int>(2);
                    foreach (int id in new[] { bottom, right, top, left }) if (id >= 0) e.Add(id);
                    Link(e[0], e[1]);
                }
            }

        var used = new HashSet<int>();
        foreach (int start in links.Keys)
        {
            if (used.Contains(start)) continue;
            var pts = new List<Vector2>();
            int prev = -1, cur = start;
            while (cur >= 0 && used.Add(cur))
            {
                pts.Add(pos[cur]);
                int next = -1;
                foreach (int n in links[cur]) if (n != prev && !used.Contains(n)) { next = n; break; }
                prev = cur;
                cur = next;
            }
            if (pts.Count >= 4)
            {
                outLines.Add(pts);
                outClosed.Add(true);
            }
        }
    }

    // ---------------------------------------------------------------- smoothing

    // RDP -> pieces of at most 4 radii -> Chaikin x2 (corners rounded with a radius of about a tube radius, like bent
    // glass) -> RDP again with 5 % of the radius, so straight runs and gentle curves keep few rings.
    static List<Vector2> Smooth(List<Vector2> pts, bool closed, float r)
    {
        var p = Rdp(pts, closed, 0.7f);
        float maxLen = Mathf.Max(4 * r, 4);
        var sub = new List<Vector2>();
        int n = p.Count, segs = closed ? n : n - 1;
        for (int i = 0; i < segs; i++)
        {
            Vector2 a = p[i], b = p[(i + 1) % n];
            int k = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / maxLen));
            for (int j = 0; j < k; j++) sub.Add(Vector2.Lerp(a, b, (float)j / k));
        }
        if (!closed) sub.Add(p[n - 1]);
        for (int it = 0; it < 2; it++) sub = Chaikin(sub, closed);
        return Rdp(sub, closed, Mathf.Max(0.05f * r, 0.05f));
    }

    static List<Vector2> Chaikin(List<Vector2> p, bool closed)
    {
        int n = p.Count;
        if (n < 3) return p;
        var o = new List<Vector2>(n * 2);
        if (!closed) o.Add(p[0]);
        int segs = closed ? n : n - 1;
        for (int i = 0; i < segs; i++)
        {
            Vector2 a = p[i], b = p[(i + 1) % n];
            if (closed || i > 0) o.Add(Vector2.Lerp(a, b, 0.25f));
            if (closed || i < segs - 1) o.Add(Vector2.Lerp(a, b, 0.75f));
        }
        if (!closed) o.Add(p[n - 1]);
        return o;
    }

    static List<Vector2> Rdp(List<Vector2> p, bool closed, float eps)
    {
        if (p.Count < 3) return new List<Vector2>(p);
        if (closed)
        {
            // split the loop at the point farthest from the first one and simplify both halves
            int far = 0;
            for (int i = 1; i < p.Count; i++) if ((p[i] - p[0]).sqrMagnitude > (p[far] - p[0]).sqrMagnitude) far = i;
            var a = Rdp(p.GetRange(0, far + 1), false, eps);
            var tail = p.GetRange(far, p.Count - far);
            tail.Add(p[0]);
            var b = Rdp(tail, false, eps);
            a.RemoveAt(a.Count - 1);
            b.RemoveAt(b.Count - 1);
            a.AddRange(b);
            return a;
        }
        var keep = new bool[p.Count];
        keep[0] = keep[p.Count - 1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, p.Count - 1));
        while (stack.Count > 0)
        {
            var (s, e) = stack.Pop();
            float best = 0;
            int idx = -1;
            Vector2 d = p[e] - p[s];
            float len = d.magnitude;
            for (int i = s + 1; i < e; i++)
            {
                Vector2 v = p[i] - p[s];
                float dist = len > 1e-6f ? Mathf.Abs(d.x * v.y - d.y * v.x) / len : v.magnitude;
                if (dist > best) { best = dist; idx = i; }
            }
            if (idx >= 0 && best > eps)
            {
                keep[idx] = true;
                stack.Push((s, idx));
                stack.Push((idx, e));
            }
        }
        var o = new List<Vector2>();
        for (int i = 0; i < p.Count; i++) if (keep[i]) o.Add(p[i]);
        return o;
    }

    // ---------------------------------------------------------------- tube mesh

    // Cross-section of the tube in units of the radius: x = sideways in the letter plane, y = depth (+ = away from the
    // viewer; the letters face -Z). A sharp corner has one point per face, so the faces stay flat.
    class Profile
    {
        public readonly List<Vector2> pos = new List<Vector2>(), nrm = new List<Vector2>();
        public readonly List<int> segs = new List<int>();          // pairs of points joined into a strip along the tube
        public readonly List<Vector2> outline = new List<Vector2>();   // square: the flat end face, corner by corner
        public bool round;
    }

    static Profile MakeProfile(Shape shape, Back back)
    {
        var pr = new Profile { round = shape == Shape.Round };
        bool half = back == Back.Open;
        if (pr.round)
        {
            int n = half ? Sides / 2 + 1 : Sides;   // half: from one side round the front to the other side
            for (int k = 0; k < n; k++)
            {
                float a = half ? Mathf.PI + Mathf.PI * k / (n - 1) : 2 * Mathf.PI * k / Sides;
                var p = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                pr.pos.Add(p);
                pr.nrm.Add(p);
            }
            for (int k = 0; k < (half ? n - 1 : n); k++) { pr.segs.Add(k); pr.segs.Add((k + 1) % n); }
        }
        else
        {
            // a square 2 radii wide: right, front, left and back faces; the open one stops at the middle (depth 0),
            // a flat strip one radius deep, like an LED neon strip on a wall
            Vector2[] c = half
                ? new[] { new Vector2(1, 0), new Vector2(1, -1), new Vector2(-1, -1), new Vector2(-1, 0) }
                : new[] { new Vector2(1, 1), new Vector2(1, -1), new Vector2(-1, -1), new Vector2(-1, 1) };
            int faces = half ? 3 : 4;
            for (int f = 0; f < faces; f++)
            {
                Vector2 a = c[f], b = c[(f + 1) % 4], mid = (a + b) / 2;
                var n = Mathf.Abs(mid.x) > Mathf.Abs(mid.y) ? new Vector2(Mathf.Sign(mid.x), 0) : new Vector2(0, Mathf.Sign(mid.y));
                pr.segs.Add(pr.pos.Count);
                pr.segs.Add(pr.pos.Count + 1);
                pr.pos.Add(a);
                pr.pos.Add(b);
                pr.nrm.Add(n);
                pr.nrm.Add(n);
            }
            pr.outline.AddRange(c);
        }
        return pr;
    }

    // Tubes of one profile along lines, into one mesh. Sharp profiles also keep smooth "halo normals" (straight out of
    // the tube's axis) for uv3: the halo of Custom/Neon is the mesh blown up along the normals, and the split normals
    // of flat faces would tear that shell open at every edge.
    class TubeMesh
    {
        public readonly List<Vector3> verts = new List<Vector3>(), normals = new List<Vector3>(), haloNormals = new List<Vector3>();
        public readonly List<Vector4> letter = new List<Vector4>();
        public readonly List<int> tris = new List<int>();
        readonly Profile pr;

        public TubeMesh(Profile profile) { pr = profile; }
        public bool Sharp => !pr.round;

        public void Add(List<Vector2> pts, bool closed, float r, Vector4 l)
        {
            int n = pts.Count;
            if (n < 2) return;
            if (!pr.round) { AddRuns(pts, closed, r, l); return; }
            var tangent = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                Vector2 prev = closed ? pts[(i - 1 + n) % n] : pts[Mathf.Max(i - 1, 0)];
                Vector2 next = closed ? pts[(i + 1) % n] : pts[Mathf.Min(i + 1, n - 1)];
                tangent[i] = (next - prev).normalized;
            }

            int first = verts.Count, size = pr.pos.Count;
            for (int i = 0; i < n; i++) Ring(pts[i], tangent[i], r, 1, 0, l);
            for (int i = 0; i < (closed ? n : n - 1); i++) Bridge(first + i * size, first + (i + 1) % n * size);

            if (closed) return;
            End(pts[0], tangent[0], -1, first, r, l);
            End(pts[n - 1], tangent[n - 1], 1, first + (n - 1) * size, r, l);
        }

        // Sharp profile: straight runs between the corners of the line, every face flat (each run has rings of its own,
        // with its own face normals). A corner is mitred (the rings sit on the bisector, the sides stretched by
        // 1 / cos(half the turn), so the strip keeps its width) when it turns at most MitreLimit degrees and the mitre,
        // r * tan(half the turn) along each run, takes at most half of both runs: else the inner side would cross over
        // and turn the faces inside out (short runs round the curves of Outline letters). Other corners are bevelled:
        // both runs end square at the corner and a piece with the bisector's normals closes the outside (its inner side
        // lies inside the runs).
        const float MitreLimit = 90;
        enum Joint { None, Straight, Mitre, Bevel }

        Vector2[] runDir;
        float[] runLen;
        bool runsClosed;

        Joint JointOf(int run, int other, float r)
        {
            int runs = runDir.Length;
            if (runsClosed) other = (other + runs) % runs;
            if (other < 0 || other >= runs) return Joint.None;
            float turn = Vector2.Angle(runDir[run], runDir[other]);
            if (turn < 0.5f) return Joint.Straight;
            float mitre = r * Mathf.Tan(0.5f * turn * Mathf.Deg2Rad);
            return turn <= MitreLimit && mitre <= 0.5f * Mathf.Min(runLen[run], runLen[other]) ? Joint.Mitre : Joint.Bevel;
        }

        void AddRuns(List<Vector2> pts, bool closed, float r, Vector4 l)
        {
            int n = pts.Count, runs = closed ? n : n - 1;
            runDir = new Vector2[runs];
            runLen = new float[runs];
            runsClosed = closed;
            for (int i = 0; i < runs; i++)
            {
                Vector2 d = pts[(i + 1) % n] - pts[i];
                runLen[i] = d.magnitude;
                runDir[i] = d.normalized;
            }

            int firstStart = -1, lastEnd = -1;
            for (int i = 0; i < runs; i++)
            {
                int start = RunEnd(pts[i], i, i - 1, r, l);
                int end = RunEnd(pts[(i + 1) % n], i, i + 1, r, l);
                Bridge(start, end);
                if (i == 0) firstStart = start;
                lastEnd = end;

                // bevel piece at the corner after this run
                if (JointOf(i, i + 1, r) != Joint.Bevel) continue;
                Vector2 dIn = runDir[i], dOut = runDir[(i + 1) % runs], p = pts[(i + 1) % n];
                Vector2 face = Perp((dIn + dOut).normalized);
                int a = RingAt(p, Perp(dIn), 1, face, r, l);
                int b = RingAt(p, Perp(dOut), 1, face, r, l);
                Bridge(a, b);
            }
            if (closed) return;
            End(pts[0], runDir[0], -1, firstStart, r, l);
            End(pts[n - 1], runDir[runs - 1], 1, lastEnd, r, l);
        }

        // ring of run `run` at the line point p, where it meets run `other` (none at the ends of an open line)
        int RunEnd(Vector2 p, int run, int other, float r, Vector4 l)
        {
            Vector2 d = runDir[run], face = Perp(d);
            if (JointOf(run, other, r) != Joint.Mitre) return RingAt(p, face, 1, face, r, l);   // square end or corner
            int runs = runDir.Length;
            Vector2 mitre = Perp((d + runDir[(other + runs) % runs]).normalized);
            return RingAt(p, mitre, 1 / Vector2.Dot(mitre, face), face, r, l);
        }

        static Vector2 Perp(Vector2 d) => new Vector2(-d.y, d.x);

        // the profile at p with its sideways axis along `across` (stretched by `stretch`); faces get the normals of a run
        // whose sideways normal is `face`
        int RingAt(Vector2 p, Vector2 across, float stretch, Vector2 face, float r, Vector4 l)
        {
            int first = verts.Count;
            var side = new Vector3(across.x, across.y, 0);
            var faceSide = new Vector3(face.x, face.y, 0);
            for (int k = 0; k < pr.pos.Count; k++)
            {
                Vector3 offset = side * (pr.pos[k].x * stretch) + Vector3.forward * pr.pos[k].y;
                verts.Add(new Vector3(p.x, p.y, 0) + offset * r);
                normals.Add(faceSide * pr.nrm[k].x + Vector3.forward * pr.nrm[k].y);
                haloNormals.Add(offset.normalized);
                letter.Add(l);
            }
            return first;
        }

        // the profile around c, across the tangent t; scale = share of the radius, shift = along t (both in r)
        void Ring(Vector2 c, Vector2 t, float r, float scale, float shift, Vector4 l)
        {
            var side = new Vector3(-t.y, t.x, 0);
            var along = new Vector3(t.x, t.y, 0);
            for (int k = 0; k < pr.pos.Count; k++)
            {
                Vector3 offset = (side * pr.pos[k].x + Vector3.forward * pr.pos[k].y) * scale + along * shift;
                verts.Add(new Vector3(c.x, c.y, 0) + offset * r);
                // round: the offset is a unit vector, on the tube and on the half-sphere ends alike
                normals.Add(pr.round ? offset.normalized : side * pr.nrm[k].x + Vector3.forward * pr.nrm[k].y);
                haloNormals.Add(offset.normalized);
                letter.Add(l);
            }
        }

        void Bridge(int ringA, int ringB)
        {
            for (int s = 0; s < pr.segs.Count; s += 2)
            {
                int a = pr.segs[s], b = pr.segs[s + 1];
                Tri(ringA + a, ringB + a, ringB + b);
                Tri(ringA + a, ringB + b, ringA + b);
            }
        }

        // closes the tube at c, outwards along sign * t; ring0 = the tube's last ring there.
        // Round: a half-sphere. Sharp: the strip runs on one radius (as far as a round end would reach) and ends flat.
        void End(Vector2 c, Vector2 t, float sign, int ring0, float r, Vector4 l)
        {
            var along = new Vector3(t.x, t.y, 0) * sign;
            var side = new Vector3(-t.y, t.x, 0);
            int prev = ring0;
            if (pr.round)
            {
                for (int j = 1; j < CapRings; j++)
                {
                    float phi = 0.5f * Mathf.PI * j / CapRings;
                    int ring = verts.Count;
                    Ring(c, t, r, Mathf.Cos(phi), sign * Mathf.Sin(phi), l);
                    Bridge(prev, ring);
                    prev = ring;
                }
                int pole = verts.Count;
                verts.Add(new Vector3(c.x, c.y, 0) + along * r);
                normals.Add(along);
                haloNormals.Add(along);
                letter.Add(l);
                for (int s = 0; s < pr.segs.Count; s += 2) Tri(prev + pr.segs[s], prev + pr.segs[s + 1], pole);
                return;
            }
            int last = verts.Count;
            Ring(c, t, r, 1, sign, l);
            Bridge(prev, last);
            int face = verts.Count;
            Vector3 centre = new Vector3(c.x, c.y, 0) + along * r;
            foreach (var o in pr.outline)
            {
                Vector3 offset = side * o.x + Vector3.forward * o.y;
                verts.Add(centre + offset * r);
                normals.Add(along);
                haloNormals.Add((offset + along).normalized);
                letter.Add(l);
            }
            for (int k = 1; k + 1 < pr.outline.Count; k++) Tri(face, face + k, face + k + 1);
        }

        // adds the triangle facing its vertex normals (Unity: front = clockwise, normal = cross(b - a, c - a))
        void Tri(int a, int b, int c)
        {
            Vector3 f = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]);
            if (Vector3.Dot(f, normals[a] + normals[b] + normals[c]) < 0) (b, c) = (c, b);
            tris.Add(a);
            tris.Add(b);
            tris.Add(c);
        }
    }

    // ---------------------------------------------------------------- distance transform

    // Squared distance from every pixel to the nearest pixel with ink == feature (Felzenszwalb & Huttenlocher).
    static float[] Edt(bool[] ink, int W, int H, bool feature)
    {
        const double Inf = 1e20;
        int n = Mathf.Max(W, H);
        var g = new double[W * H];
        for (int p = 0; p < g.Length; p++) g[p] = ink[p] == feature ? 0 : Inf;
        var f = new double[n];
        var d = new double[n];
        var v = new int[n];
        var z = new double[n + 1];
        for (int x = 0; x < W; x++)
        {
            for (int y = 0; y < H; y++) f[y] = g[y * W + x];
            Edt1(f, H, d, v, z);
            for (int y = 0; y < H; y++) g[y * W + x] = d[y];
        }
        var r = new float[W * H];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++) f[x] = g[y * W + x];
            Edt1(f, W, d, v, z);
            for (int x = 0; x < W; x++) r[y * W + x] = (float)d[x];
        }
        return r;
    }

    static void Edt1(double[] f, int n, double[] d, int[] v, double[] z)
    {
        int k = 0;
        v[0] = 0;
        z[0] = double.NegativeInfinity;
        z[1] = double.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            double s;
            while (true)
            {
                int vk = v[k];
                s = (f[q] + (double)q * q - f[vk] - (double)vk * vk) / (2.0 * (q - vk));
                if (s > z[k]) break;
                k--;
            }
            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = double.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            double dq = q - v[k];
            d[q] = dq * dq + f[v[k]];
        }
    }
}
