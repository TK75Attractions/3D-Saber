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
    public float judgeWindow = 0.23f;
    // 判定ウィンドウを過ぎたら自動的に miss 扱いにする。
    public float missGrace = 0.07f;
    // miss 扱いになった後、ノーツを画面後方まで流してから片付けるまでの秒数。
    public float despawnAfterMissSeconds = 1.5f;

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
            if (n != null) SafeDestroy(n.gameObject);
        }
        liveNotes.Clear();
    }

    private static void SafeDestroy(GameObject go)
    {
        if (go == null) return;
        if (Application.isPlaying) Destroy(go);
        else DestroyImmediate(go);
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
        note.RequiredDirection = CutDirectionHelper.Parse(nd.direction);
        note.RequiredCutCount = Mathf.Max(1, nd.count);
        note.RemainingCuts = note.RequiredCutCount;

        // 方向指定なら矢印マーカー
        if (note.RequiredDirection != CutDirection.None)
        {
            BuildArrow(go.transform, note.RequiredDirection);
        }
        // ロングは Z 方向に伸ばす（カウント表示は不要、ひびで進捗を見せる）
        if (note.RequiredCutCount > 1)
        {
            Vector3 sc = go.transform.localScale;
            go.transform.localScale = new Vector3(sc.x, sc.y, sc.z * note.RequiredCutCount);
        }

        liveNotes.Add(note);
        OnNoteSpawned?.Invoke(note);
    }

    private static void BuildArrow(Transform parent, CutDirection dir)
    {
        GameObject arrow = new GameObject("Arrow");
        arrow.transform.SetParent(parent, false);
        arrow.transform.localPosition = new Vector3(0f, 0f, -0.55f);
        arrow.transform.localRotation = Quaternion.Euler(0f, 0f, CutDirectionHelper.ToZRotationDegrees(dir));

        // 上向き ^ シェブロン
        for (int sign = -1; sign <= 1; sign += 2)
        {
            GameObject bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bar.name = sign < 0 ? "BarL" : "BarR";
            bar.transform.SetParent(arrow.transform, false);
            bar.transform.localPosition = new Vector3(sign * 0.13f, -0.06f, 0f);
            bar.transform.localRotation = Quaternion.Euler(0f, 0f, sign * 35f);
            bar.transform.localScale = new Vector3(0.06f, 0.32f, 0.04f);
            var col = bar.GetComponent<BoxCollider>();
            if (col != null) Destroy(col);
            var mr = bar.GetComponent<Renderer>();
            if (mr != null)
            {
                var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                var mat = new Material(sh);
                Color yellow = new Color(1f, 0.95f, 0.3f);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", yellow);
                else mat.color = yellow;
                if (mat.HasProperty("_EmissionColor"))
                {
                    mat.EnableKeyword("_EMISSION");
                    mat.SetColor("_EmissionColor", yellow * 1.6f);
                }
                mr.sharedMaterial = mat;
            }
        }
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

            // ロングノーツは複数回切る時間が必要なので、後方の窓を回数に応じて伸ばす。
            float lateWindow = note.RequiredCutCount > 1
                ? judgeWindow + (note.RequiredCutCount - 1) * 0.35f
                : judgeWindow;
            note.IsJudgeable = dt <= judgeWindow && dt >= -lateWindow;

            if (note.IsCut)
            {
                liveNotes.RemoveAt(i);
                continue;
            }

            // 判定窓を過ぎた瞬間に Miss を1回だけ発火するが、ノーツは消さずに後ろへ流し続ける。
            if (!note.IsMissed && dt < -(lateWindow + missGrace))
            {
                note.MarkMiss();
                OnNoteMissed?.Invoke(note);
            }

            // 後方に十分流れたら回収。
            if (note.IsMissed && dt < -(lateWindow + missGrace + despawnAfterMissSeconds))
            {
                SafeDestroy(note.gameObject);
                liveNotes.RemoveAt(i);
            }
        }
    }
}
