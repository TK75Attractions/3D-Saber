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
    // ノーツが判定可能になる時間幅。Classify の Good 上限 (210ms) と合わせて 0.21s。
    public float judgeWindow = 0.21f;
    // 判定ウィンドウを過ぎたら自動的に miss 扱いにする猶予秒数（Bad 上限まで届くように）。
    public float missGrace = 0.06f;
    // miss 扱いになった後、ノーツを画面後方まで流してから片付けるまでの秒数。
    public float despawnAfterMissSeconds = 1.5f;
    // ロングノーツの追加カット1回あたりの判定ウィンドウ延長秒。
    // 4 回切りなら lateWindow = judgeWindow + 3 * secondsPerLongCut ≒ 2.3s。
    // 50 回切りなら ≒ 34.5s（25秒の「サビ前ロング」も余裕で表現可能）。
    public float secondsPerLongCut = 0.7f;
    // ロングノーツが HitTime 経過後、判定面の少し後方に滞留する距離（プレイヤーが切り続けられるように）。
    public float longLingerDriftZ = 1.0f;
    // ロングノーツの見た目の Z スケール上限（count=50 等でも視野を埋め尽くさないようにキャップ）。
    public float longMaxVisualZScale = 6f;

    private ChartData chart;
    private int nextIndex;
    private double totalOffsetSeconds; // chart.offsetMs/1000 + extraOffsetSeconds
    private double extraOffsetSeconds; // GamePlayManager から実行時に上書き
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
        RecomputeTotalOffset();
        foreach (var n in liveNotes)
        {
            if (n != null) SafeDestroy(n.gameObject);
        }
        liveNotes.Clear();
    }

    public void SetExtraOffsetSeconds(double seconds)
    {
        extraOffsetSeconds = seconds;
        RecomputeTotalOffset();
    }

    public double TotalOffsetSeconds => totalOffsetSeconds;

    private void RecomputeTotalOffset()
    {
        double chartOffset = chart != null ? chart.offsetMs / 1000.0 : 0.0;
        totalOffsetSeconds = chartOffset + extraOffsetSeconds;
    }

    // 譜面ノートの「実効時刻（秒）」。chart.offsetMs と extraOffsetSeconds を加算済み。
    public double EffectiveTime(NoteData nd)
    {
        return nd.TimeSeconds + totalOffsetSeconds;
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
            double eff = EffectiveTime(nd);
            if (songTime + approachTime < eff) break;
            SpawnOne(nd);
            nextIndex++;
        }
    }

    private void SpawnOne(NoteData nd)
    {
        GameObject prefab = PickPrefab(nd.color);
        if (prefab == null) return;
        float scale = chart != null ? chart.coordScale : 1f;
        Vector3 pos = new Vector3(nd.x * scale, nd.y * scale, spawnZ);
        GameObject go = Instantiate(prefab, pos, Quaternion.identity, noteRoot);
        CuttableNote note = go.GetComponent<CuttableNote>();
        if (note == null)
        {
            note = go.AddComponent<CuttableNote>();
        }
        note.HitTime = EffectiveTime(nd);
        note.RequiredDirection = CutDirectionHelper.Parse(nd.direction);
        note.RequiredCutCount = Mathf.Max(1, nd.count);
        note.RemainingCuts = note.RequiredCutCount;
        // 金ノーツ判定（chart.json の color:"gold"）
        note.IsGold = !string.IsNullOrEmpty(nd.color) && nd.color.ToLowerInvariant() == "gold";

        // 旧プレハブにある面ステッカー等を剥がして、クリスタル＋ネオン外観に置き換える。
        // NoteVisuals 自身が冪等で、既存プレハブにも安全に被せられる。
        if (go.GetComponent<NoteVisuals>() == null) go.AddComponent<NoteVisuals>();

        // 方向指定なら矢印マーカー
        if (note.RequiredDirection != CutDirection.None)
        {
            BuildArrow(go.transform, note.RequiredDirection);
        }
        // ロングは Z 方向に伸ばし、上に残カウント数字を浮かべる。
        // count が大きすぎる（50 など）と視野を覆い尽くすので longMaxVisualZScale でキャップする。
        if (note.RequiredCutCount > 1)
        {
            Vector3 sc = go.transform.localScale;
            float zFactor = Mathf.Min(note.RequiredCutCount, longMaxVisualZScale);
            go.transform.localScale = new Vector3(sc.x, sc.y, sc.z * zFactor);
            BuildCountLabel(go.transform, note);
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

    private static void BuildCountLabel(Transform target, CuttableNote note)
    {
        // 親にしない：ロングノーツの Z スケール拡張に引きずられて位置や形が歪まないように。
        // 別 GameObject + FollowTransformWorldOffset で追従させる。
        var go = new GameObject("CountLabel");
        Vector3 offset = new Vector3(0f, 0.7f, 0f);
        go.transform.position = target.position + offset;
        // プレイヤー（カメラ）は -Z 側。テキストの前面（+Z）を -Z に向けるため 180° 回転。
        // それだけだと水平方向に反転するので X スケールを負にして打ち消す。
        go.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
        go.transform.localScale = new Vector3(-1.0f, 1.0f, 1.0f);

        var tmp = go.AddComponent<TMPro.TextMeshPro>();
        tmp.text = note.RequiredCutCount.ToString();
        tmp.fontSize = 12f;
        tmp.fontStyle = TMPro.FontStyles.Bold;
        tmp.alignment = TMPro.TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.color = Color.white;

        var follower = go.AddComponent<FollowTransformWorldOffset>();
        follower.target = target;
        follower.worldOffset = offset;

        note.countLabel = tmp;
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
            float z = ComputeNoteZ(note, dt, speed);
            Vector3 p = note.transform.position;
            note.transform.position = new Vector3(p.x, p.y, z);

            // ロングノーツは複数回切る時間が必要なので、後方の窓を回数に応じて伸ばす。
            float lateWindow = LateWindowFor(note);
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

    // テスト用に公開：ロングの判定ウィンドウは秒数で何秒か。
    public float LateWindowFor(CuttableNote note)
    {
        if (note == null) return judgeWindow;
        return note.RequiredCutCount > 1
            ? judgeWindow + (note.RequiredCutCount - 1) * secondsPerLongCut
            : judgeWindow;
    }

    // ロングノーツは HitTime 経過後に判定面の少し後方で「滞留」させて、プレイヤーが切り続けやすくする。
    // テスト用に公開（純粋関数）。
    public float ComputeNoteZ(CuttableNote note, double dt, float speed)
    {
        if (note == null || note.RequiredCutCount <= 1 || dt >= 0)
        {
            return judgeZ + speed * (float)dt;
        }
        double overshoot = -dt;
        float lingerDuration = (note.RequiredCutCount - 1) * secondsPerLongCut;
        if (lingerDuration <= 0.001f || overshoot <= lingerDuration)
        {
            float progress = lingerDuration > 0.001f ? (float)(overshoot / lingerDuration) : 1f;
            return judgeZ - longLingerDriftZ * progress;
        }
        double afterLinger = overshoot - lingerDuration;
        return judgeZ - longLingerDriftZ - speed * (float)afterLinger;
    }
}
