using System.Collections.Generic;
using UnityEngine;

// 方向矢印が作った材質だけを所有する。本体や別ノーツの共有材質には触れない。
public sealed class NoteArrowMaterials : MonoBehaviour
{
    readonly List<Mesh> meshes = new List<Mesh>();
    internal void Register(Mesh mesh) { meshes.Add(mesh); }
    readonly List<Material> owned = new List<Material>();

    public static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    public const float FlashSeconds = .2f;
    Material bars;
    CuttableNote note;
    Color noteColor;
    float flashAge = FlashSeconds;
    float cueAlpha = 1f;

    internal void Register(Material material, bool isBars = false)
    {
        owned.Add(material);
        if (isBars) bars = material;
    }

    // Q の隠した向き(CuttableNote.HidesSideArrow)。本当の矢印の代わりに左右の両矢印(↔)を出す。部品は本当の矢印と同じ
    // 2つの材質(黒い縁取り・白い棒)を使うので、見えにくさ・暗さ・光り方は本当の矢印と同じ。違う向きに振られたら
    // (Flash)本当の矢印へ戻して光らせる。使い回しの矢印は出現のたびに NoteSpawner が SetSideArrow で決め直す。
    GameObject[] arrowParts, sideParts;
    public bool SideShown { get; private set; }

    internal void SetSideArrow(bool side)
    {
        FindArrowParts();
        if (side && sideParts == null) BuildSideParts();
        SideShown = side && sideParts != null;
        if (arrowParts != null)
            foreach (var part in arrowParts) if (part != null && part.activeSelf == SideShown) part.SetActive(!SideShown);
        if (sideParts != null)
            foreach (var part in sideParts) if (part != null && part.activeSelf != SideShown) part.SetActive(SideShown);
    }

    // 部品(テスト用)。side が false なら本当の矢印、layer 0 が黒い縁取り・1 が白い棒。
    internal GameObject Part(bool side, int layer)
    {
        FindArrowParts();
        var parts = side ? sideParts : arrowParts;
        return parts != null && layer >= 0 && layer < parts.Length ? parts[layer] : null;
    }

    // NoteSpawner.BuildArrow が作った本当の矢印の2枚。
    void FindArrowParts()
    {
        if (arrowParts != null) return;
        var backingPart = transform.Find("ArrowBacking");
        var barsPart = transform.Find("Bars");
        arrowParts = new[] { backingPart != null ? backingPart.gameObject : null, barsPart != null ? barsPart.gameObject : null };
    }

    void BuildSideParts()
    {
        Material backing = null;
        foreach (var material in owned)
            if (material != null && material != bars) { backing = material; break; }
        if (bars == null || backing == null) return;
        var parts = new GameObject[2];
        for (int layer = 0; layer < 2; layer++)
        {
            var mesh = FlickArrowShape.CreateSideMesh(layer == 0);
            Register(mesh);
            var part = new GameObject(layer == 0 ? "SideBacking" : "SideBars", typeof(MeshFilter), typeof(MeshRenderer));
            part.layer = gameObject.layer;
            part.transform.SetParent(transform, false);
            // 矢印の根は本当の向きへ回してあるので、両矢印はその回転を打ち消してノーツの横に向ける。
            part.transform.localRotation = Quaternion.Inverse(transform.localRotation);
            part.transform.localPosition = new Vector3(0, 0, layer == 0 ? .008f : -.008f);
            part.transform.localScale = Vector3.one;
            part.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = part.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = layer == 0 ? backing : bars;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            // 並び順は本当の矢印の部品に合わせる(試験用紙の上へ上げた値も写す)。
            var source = arrowParts != null && arrowParts[layer] != null ? arrowParts[layer].GetComponent<MeshRenderer>() : null;
            if (source != null)
            {
                renderer.sortingLayerID = source.sortingLayerID;
                renderer.sortingOrder = source.sortingOrder;
            }
            part.SetActive(false);
            parts[layer] = part;
        }
        sideParts = parts;
    }

    internal void Bind(CuttableNote target, Color color)
    {
        if (note != null) note.OnWrongDirection -= Flash;
        note = target;
        noteColor = color;
        SetVeil(note != null ? note.Veil : 0f);
        ResetCue();
        if (note == null) return;
        note.Arrow = this;
        note.OnWrongDirection -= Flash;
        note.OnWrongDirection += Flash;
    }

    // 色を一瞬乗せ、0.2 秒で白へ戻す。黒い下敷きの材質には触れない。
    public static Color FlashColor(Color color, float age)
        => Color.Lerp(color, Color.white, Mathf.Clamp01(age / FlashSeconds));

    internal void RefreshDarkBrightness()
    {
        if (bars != null) bars.SetColor(BaseColorId, WithVeil(FlashColor(noteColor, flashAge)));
    }

    // 消える(V)のように見えにくさが毎コマ変わるノーツで、今の Veil に合わせ直す。
    internal void RefreshVeil() => SetVeil(note != null ? note.Veil : 0f);

    void SetVeil(float veil)
    {
        float alpha = NoteVeil.CueAlpha(veil);
        if (cueAlpha != alpha)
        {
            cueAlpha = alpha;
            foreach (var material in owned) NoteVeil.SetAlpha(material, alpha);
        }
        bool shown = veil < MemoryVeilLayers.HideCuesAtVeil
            && (note == null || !note.HidesRevealArrow);
        if (gameObject.activeSelf != shown) gameObject.SetActive(shown);
    }

    Color WithVeil(Color color)
    {
        if (cueAlpha != 1f) color.a *= cueAlpha;
        return DarkRules.Rgb(color, note != null ? note.Brightness : 1f);
    }

    void Flash(CuttableNote source)
    {
        if (source.DirectionVisualOnly || source.IsMissed || source.HidesCues || bars == null) return;
        // 隠した向き: 違う向きに振られたら、両矢印を本当の向きの矢印へ戻して光らせる(どちらが正しかったかを見せる)。
        if (SideShown)
        {
            source.RevealSideArrow();
            SetSideArrow(false);
        }
        flashAge = 0f;
        bars.SetColor(BaseColorId, WithVeil(FlashColor(noteColor, flashAge)));
    }

    internal void Tick(float dt)
    {
        if (flashAge >= FlashSeconds || bars == null) return;
        if (note == null || note.IsMissed || note.IsFinalized) { ResetCue(); return; }
        flashAge = Mathf.Min(FlashSeconds, flashAge + Mathf.Max(0f, dt));
        bars.SetColor(BaseColorId, WithVeil(FlashColor(noteColor, flashAge)));
    }

    internal void ResetCue()
    {
        flashAge = FlashSeconds;
        if (bars != null) bars.SetColor(BaseColorId, WithVeil(Color.white));
    }

    void OnDisable() { ResetCue(); }

    void OnDestroy()
    {
        if (note != null)
        {
            note.OnWrongDirection -= Flash;
            if (note.Arrow == this) note.Arrow = null;
        }
        // 切断・ミス回収・譜面リセット・選曲の再生成・シーン退出で共通して解放する。
        foreach (var material in owned)
        {
            if (material == null) continue;
            if (Application.isPlaying) Destroy(material);
            else DestroyImmediate(material);
        }
        owned.Clear();
        foreach (var mesh in meshes) UISkinKit.SafeDestroy(mesh);
        meshes.Clear();
    }
}
