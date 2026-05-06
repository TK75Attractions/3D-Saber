using System.Collections.Generic;
using UnityEngine;

// chart.json の BPM から小節線を生成し、ノーツと同じ速度で奥から手前に流す。
// 4/4 拍子前提で 4 拍ごとに 1 本（beatsPerBar で変更可）。
public class BarLineSpawner : MonoBehaviour
{
    public GameObject barLinePrefab;
    public Transform root;
    public float approachTime = 2.0f;
    public float spawnZ = 20f;
    public float judgeZ = 0f;
    public float despawnAfterSeconds = 1.5f;
    public int beatsPerBar = 4;
    // バー番号が変わる際に強調する間隔（0 で無効）。控えめにするため既定は 0。
    public int accentEvery = 0;

    private float bpm;
    private double endTimeSeconds;
    private int nextBarIndex;
    private readonly List<(GameObject obj, double time)> live = new List<(GameObject, double)>();

    public float Speed => approachTime > 0.0001f ? (spawnZ - judgeZ) / approachTime : 0f;

    public int AliveCount => live.Count;
    public int NextIndex => nextBarIndex;

    public void SetChart(ChartData chart)
    {
        bpm = chart != null ? chart.bpm : 0f;
        if (chart != null && chart.notes != null && chart.notes.Count > 0)
        {
            endTimeSeconds = chart.notes[chart.notes.Count - 1].TimeSeconds + 4.0;
        }
        else
        {
            endTimeSeconds = 0.0;
        }
        nextBarIndex = 0;
        Cleanup();
    }

    public void Tick(double songTime)
    {
        SpawnDue(songTime);
        UpdateLive(songTime);
    }

    private void SpawnDue(double songTime)
    {
        if (bpm <= 0f) return;
        double barInterval = 60.0 / bpm * beatsPerBar;
        if (barInterval <= 0.0) return;
        while (true)
        {
            double barTime = nextBarIndex * barInterval;
            if (barTime > endTimeSeconds) break;
            if (songTime + approachTime < barTime) break;
            SpawnBar(barTime);
            nextBarIndex++;
        }
    }

    private void SpawnBar(double barTime)
    {
        if (barLinePrefab == null) return;
        var go = Instantiate(barLinePrefab, new Vector3(0f, 0f, spawnZ), Quaternion.identity, root);
        go.SetActive(true);
        // accentEvery 毎に強調（少し明るく）
        if (accentEvery > 0 && nextBarIndex % accentEvery == 0)
        {
            var mr = go.GetComponentInChildren<MeshRenderer>();
            if (mr != null && mr.sharedMaterial != null)
            {
                var mat = new Material(mr.sharedMaterial);
                if (mat.HasProperty("_BaseColor"))
                {
                    Color c = mat.GetColor("_BaseColor");
                    mat.SetColor("_BaseColor", c * 1.6f);
                    if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", c * 2.0f);
                }
                mr.sharedMaterial = mat;
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
            if (dt < -despawnAfterSeconds)
            {
                SafeDestroy(go);
                live.RemoveAt(i);
            }
        }
    }

    private void Cleanup()
    {
        foreach (var (go, _) in live)
        {
            if (go != null) SafeDestroy(go);
        }
        live.Clear();
    }

    private static void SafeDestroy(GameObject go)
    {
        if (Application.isPlaying) Destroy(go);
        else DestroyImmediate(go);
    }
}
