using System.Collections.Generic;
using UnityEngine;

// chart.json の BPM から小節線を生成し、ノーツと同じ速度で奥から手前に流す。
// 拍子変更とグリッド原点を譜面から読む。旧譜面は beatsPerBar を使用。
public class BarLineSpawner : MonoBehaviour
{
    public GameObject barLinePrefab;
    public Transform root;
    public float approachTime = 1.0f;
    public float spawnZ = 20f;
    public float judgeZ = 0f;
    public float despawnAfterSeconds = 1.5f;
    public int beatsPerBar = 4;
    // バー番号が変わる際に強調する間隔（0 で無効）。控えめにするため既定は 0。
    public int accentEvery = 0;

    // 既存プレハブの見た目を runtime で上書きするオプション（薄い白を細く保証する）。
    public bool overrideVisual = false;
    [Range(0f, 1f)] public float lineAlpha = 0.18f;
    [Range(0.005f, 0.08f)] public float lineThickness = 0.02f;

    private readonly List<double> barTimes = new List<double>();
    private int nextBarIndex;
    private Material lineMaterial;
    private Material accentMaterial;
    private readonly List<(GameObject obj, double time)> live = new List<(GameObject, double)>();
    readonly Stack<GameObject> pool = new Stack<GameObject>();
    readonly Dictionary<Material, Material> prefabAccents = new Dictionary<Material, Material>();
    MaterialPropertyBlock fadeBlock;
    double lastSongTime = double.NaN;
    public int PooledCount => pool.Count;
    public int CreatedCount { get; private set; }

    public float Speed => approachTime > 0.0001f ? (spawnZ - judgeZ) / approachTime : 0f;

    public int AliveCount => live.Count;
    public int NextIndex => nextBarIndex;

    public void SetChart(ChartData chart, double extraOffsetSeconds = 0)
    {
        nextBarIndex = 0;
        lastSongTime = double.NaN;
        Cleanup();
        barTimes.Clear();
        if (chart == null || chart.bpm <= 0 || float.IsNaN(chart.bpm) || float.IsInfinity(chart.bpm)) return;
        double offset = chart.offsetMs / 1000.0 + extraOffsetSeconds;
        double origin = chart.beatZeroMs / 1000.0 + offset;
        double end = 0;
        if (chart.notes != null && chart.notes.Count > 0)
        {
            foreach (var note in chart.notes)
                if (note != null && Finite(note.time))
                {
                    double tail = note.count > 1 ? (note.lengthMs > 0 && Finite(note.lengthMs)
                        ? note.lengthMs / 1000.0 : (note.count - 1) * .7) : 0;
                    end = System.Math.Max(end, note.TimeSeconds + tail);
                }
            end += 4.0 + offset;
        }
        var meter = new ChartMeterMap(chart.timeSignatures, beatsPerBar);
        foreach (double beat in meter.BarStarts(0, (end - origin) * chart.bpm / 60.0))
            barTimes.Add(origin + beat * 60.0 / chart.bpm);
    }

    public void Tick(double songTime)
    {
        if (!isActiveAndEnabled || !Finite(songTime)) return;
        if (Finite(lastSongTime) && songTime < lastSongTime - .001)
        {
            Cleanup();
            int index = barTimes.BinarySearch(songTime - despawnAfterSeconds);
            nextBarIndex = index < 0 ? ~index : index;
        }
        lastSongTime = songTime;
        UpdateLive(songTime);
        SpawnDue(songTime);
        UpdateLive(songTime);
    }

    private void SpawnDue(double songTime)
    {
        while (nextBarIndex < barTimes.Count)
        {
            double barTime = barTimes[nextBarIndex];
            if (songTime + approachTime + 1e-9 < barTime) break;
            // 途中からの再生でも、通過済みの線を一度に大量生成しない。
            if (barTime >= songTime - despawnAfterSeconds) SpawnBar(barTime);
            nextBarIndex++;
        }
    }

    private void SpawnBar(double barTime)
    {
        if (barLinePrefab == null) return;
        GameObject go = null;
        while (pool.Count > 0 && go == null) go = pool.Pop();
        if (go == null)
        {
            go = Instantiate(barLinePrefab, new Vector3(0f, 0f, spawnZ), Quaternion.identity, root);
            CreatedCount++;
        }
        go.SetActive(true);
        foreach (var renderer in go.GetComponentsInChildren<MeshRenderer>()) renderer.SetPropertyBlock(null);
        var sourceLine = barLinePrefab.transform.Find("Line");
        var reusedLine = go.transform.Find("Line");
        if (sourceLine != null && reusedLine != null) reusedLine.localScale = sourceLine.localScale;

        bool accented = accentEvery > 0 && nextBarIndex % accentEvery == 0;
        if (overrideVisual)
        {
            ApplyVisualOverride(go, accented);
        }

        // accentEvery 毎に強調（少し明るく）
        else
        {
            var mr = go.GetComponentInChildren<MeshRenderer>();
            var original = barLinePrefab.GetComponentInChildren<MeshRenderer>();
            if (mr != null && original != null && original.sharedMaterial != null)
            {
                var source = original.sharedMaterial;
                if (!accented) mr.sharedMaterial = source;
                else
                {
                    if (!prefabAccents.TryGetValue(source, out var mat) || mat == null)
                    {
                        mat = new Material(source) { name = "BarLine/PrefabAccent" };
                        if (mat.HasProperty("_BaseColor"))
                        {
                            Color c = source.GetColor("_BaseColor");
                            mat.SetColor("_BaseColor", c * 1.6f);
                            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", c * 2.0f);
                        }
                        prefabAccents[source] = mat;
                    }
                    mr.sharedMaterial = mat;
                }
            }
        }
        live.Add((go, barTime));
    }

    private void UpdateLive(double songTime)
    {
        float speed = Speed;
        for (int i = live.Count - 1; i >= 0; i--)
        {
            var (go, time) = live[i];
            if (go == null) { live.RemoveAt(i); continue; }
            double dt = time - songTime;
            float z = judgeZ + speed * (float)dt;
            go.transform.position = new Vector3(0f, 0f, z);
            // 生成・退場の境目だけをフェードし、小節の到達時は本来の濃さを保つ。
            if (overrideVisual)
            {
                // Unity のネイティブオブジェクトはシリアライズ時ではなく、実行時に作成する。
                if (fadeBlock == null) fadeBlock = new MaterialPropertyBlock();
                float alpha = Mathf.Clamp01((float)(approachTime - dt) / .12f)
                    * Mathf.Clamp01((float)(dt + despawnAfterSeconds) / .18f);
                foreach (var renderer in go.GetComponentsInChildren<MeshRenderer>())
                {
                    var mat = renderer.sharedMaterial;
                    if (mat == null || !mat.HasProperty("_BaseColor")) continue;
                    Color color = mat.GetColor("_BaseColor"); color.a *= alpha;
                    fadeBlock.Clear(); fadeBlock.SetColor("_BaseColor", color);
                    renderer.SetPropertyBlock(fadeBlock);
                }
            }
            if (dt < -despawnAfterSeconds)
            {
                Recycle(go);
                live.RemoveAt(i);
            }
        }
    }

    private void ApplyVisualOverride(GameObject barGo, bool accented)
    {
        // バーは prefab 内の "Line" Cube。厚みは scale.y、アルファは material.
        var line = barGo.transform.Find("Line");
        if (line != null)
        {
            Vector3 s = line.localScale;
            line.localScale = new Vector3(s.x, lineThickness, s.z);
        }
        // 元の素材が空でもURP用の薄い白を保証する。2種類だけを共有し、曲終了時に解放する。
        Material material = VisualMaterial(accented);
        var mrs = barGo.GetComponentsInChildren<MeshRenderer>();
        foreach (var mr in mrs)
        {
            mr.sharedMaterial = material;
        }
    }

    private Material VisualMaterial(bool accented)
    {
        var material = accented ? accentMaterial : lineMaterial;
        if (material == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            material = new Material(shader) { name = accented ? "BarLineAccent" : "BarLine" };
            material.SetOverrideTag("RenderType", "Transparent");
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            if (material.HasProperty("_SrcBlend")) material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (material.HasProperty("_DstBlend")) material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_ZWrite")) material.SetInt("_ZWrite", 0);
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            if (accented) accentMaterial = material; else lineMaterial = material;
        }
        float strength = accented ? 1.6f : 1f;
        Color color = new Color(strength, strength, strength, Mathf.Clamp01(lineAlpha * strength));
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        else material.color = color;
        return material;
    }

    private void OnDestroy()
    {
        Cleanup();
        while (pool.Count > 0) SafeDestroy(pool.Pop());
        foreach (var material in prefabAccents.Values) SafeDestroy(material);
        prefabAccents.Clear();
        if (lineMaterial != null) SafeDestroy(lineMaterial);
        if (accentMaterial != null) SafeDestroy(accentMaterial);
    }

    private void Cleanup()
    {
        foreach (var (go, _) in live)
        {
            if (go != null) Recycle(go);
        }
        live.Clear();
    }

    void Recycle(GameObject go)
    {
        if (go == null) return;
        go.SetActive(false);
        if (pool.Count < 24) pool.Push(go); else SafeDestroy(go);
    }
    void OnDisable() { Cleanup(); lastSongTime = double.NaN; nextBarIndex = 0; }
    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static void SafeDestroy(Object go)
    {
        if (Application.isPlaying) Destroy(go);
        else DestroyImmediate(go);
    }
}
