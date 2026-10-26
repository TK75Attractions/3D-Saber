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
