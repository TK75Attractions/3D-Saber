using System.Collections.Generic;
using UnityEngine;

// chart.json のノーツを approachTime だけ先読みで生成し、
// spawnZ → judgeZ に向けて Z 軸で流す。判定ウィンドウも更新する。
public class NoteSpawner : MonoBehaviour
{
    public GameObject notePrefab;       // デフォルト（color が red/blue のどちらでもない場合）
    public GameObject notePrefabRed;    // color="red"
    public GameObject notePrefabBlue;   // color="blue"
    public Transform noteRoot;
    public float approachTime = 2.0f;
    public float spawnZ = 20f;
    public float judgeZ = 0f;
    public float judgeWindow = 0.15f;
    // 判定ウィンドウを過ぎたら自動的に miss 扱いにする。
    public float missGrace = 0.05f;

    private ChartData chart;
    private int nextIndex;
    private readonly List<CuttableNote> liveNotes = new List<CuttableNote>();

    public float Speed => approachTime > 0.0001f ? (spawnZ - judgeZ) / approachTime : 0f;

    public int AliveCount => liveNotes.Count;
    public int NextIndex => nextIndex;

    public event System.Action<CuttableNote> OnNoteSpawned;
    public event System.Action<CuttableNote> OnNoteMissed;

    public void SetChart(ChartData data)
    {
        chart = data;
        nextIndex = 0;
        foreach (var n in liveNotes)
        {
            if (n != null) Destroy(n.gameObject);
        }
        liveNotes.Clear();
    }

    // 毎フレーム呼ぶ想定。songTime は SongPlayer.SongTime（秒）を渡す。
    public void Tick(double songTime)
    {
        SpawnDue(songTime);
        UpdateLive(songTime);
    }

    private void SpawnDue(double songTime)
    {
        if (chart == null) return;
        while (nextIndex < chart.notes.Count)
        {
            NoteData nd = chart.notes[nextIndex];
            if (songTime + approachTime < nd.TimeSeconds) break;
            SpawnOne(nd);
            nextIndex++;
        }
    }

    private void SpawnOne(NoteData nd)
    {
        GameObject prefab = PickPrefab(nd.color);
        if (prefab == null) return;
        Vector3 pos = new Vector3(nd.x, nd.y, spawnZ);
        GameObject go = Instantiate(prefab, pos, Quaternion.identity, noteRoot);
        CuttableNote note = go.GetComponent<CuttableNote>();
        if (note == null)
        {
            note = go.AddComponent<CuttableNote>();
        }
        note.HitTime = nd.TimeSeconds;
        liveNotes.Add(note);
        OnNoteSpawned?.Invoke(note);
    }

    private GameObject PickPrefab(string color)
    {
        if (!string.IsNullOrEmpty(color))
        {
            string c = color.ToLowerInvariant();
            if (c == "red" && notePrefabRed != null) return notePrefabRed;
            if (c == "blue" && notePrefabBlue != null) return notePrefabBlue;
        }
        return notePrefab;
    }

    private void UpdateLive(double songTime)
    {
        float speed = Speed;
        for (int i = liveNotes.Count - 1; i >= 0; i--)
        {
            CuttableNote note = liveNotes[i];
            if (note == null)
            {
                liveNotes.RemoveAt(i);
                continue;
            }

            // 判定時刻を時間差で表現し、現在の Z をそこから計算する。
            double dt = note.HitTime - songTime;
            float z = judgeZ + speed * (float)dt;
            Vector3 p = note.transform.position;
            note.transform.position = new Vector3(p.x, p.y, z);

            note.IsJudgeable = System.Math.Abs(dt) <= judgeWindow;

            if (note.IsCut)
            {
                liveNotes.RemoveAt(i);
                continue;
            }

            if (!note.IsMissed && dt < -(judgeWindow + missGrace))
            {
                note.MarkMiss();
                OnNoteMissed?.Invoke(note);
                liveNotes.RemoveAt(i);
            }
        }
    }
}
