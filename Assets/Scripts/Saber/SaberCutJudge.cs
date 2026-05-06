using System.Collections.Generic;
using UnityEngine;

// セーバー軌跡の XY 成分だけでノーツとの当たり判定を取る（Z は無視）。
// 「完全に通り抜けた」時点で Cut を発火させるため、
// 刃の有効範囲に入った瞬間を entry として記録し、
// 以後セーバー現在位置がノーツから離れたら exit = Cut。
public class SaberCutJudge : MonoBehaviour
{
    public SaberTracker saber;
    public float bladeRadius = 0.3f;
    public float minCutSpeed = 3.0f;
    public float maxCutDistance = 3.0f;
    public float noteHitRadiusXY = 0.5f;
    // 直近の IMU 振り検知をどれだけ古くまで採用するか
    public float imuHintMaxAgeSeconds = 0.30f;

    // GamePlayManager / GManager から RunJudge() を呼ぶときは false にして
    // 自前の Update を止め、外部から1点で呼び出す（GManager 主体パターン）。
    public bool autonomous = true;

    private struct Pending
    {
        public Vector3 hitPoint;
        public Vector3 velocity;
    }

    private readonly Dictionary<CuttableNote, Pending> pending = new Dictionary<CuttableNote, Pending>();

    public int PendingCount => pending.Count;

    void Update()
    {
        if (!autonomous) return;
        RunJudge();
    }

    public void RunJudge()
    {
        if (saber == null || !saber.HasPrevious) return;
        TryCut();
    }

    public int TryCut()
    {
        if (saber.Speed < minCutSpeed) return CheckExits();

        Vector2 from = new Vector2(saber.PreviousPosition.x, saber.PreviousPosition.y);
        Vector2 to = new Vector2(saber.CurrentPosition.x, saber.CurrentPosition.y);
        Vector2 delta = to - from;
        float dist = delta.magnitude;
        if (dist <= 0.0001f) return CheckExits();
        if (dist > maxCutDistance) return CheckExits();

        float hitRange = bladeRadius + noteHitRadiusXY;

        CuttableNote[] notes = Object.FindObjectsByType<CuttableNote>(FindObjectsSortMode.None);
        foreach (var note in notes)
        {
            if (!IsCandidate(note)) continue;
            if (pending.ContainsKey(note)) continue;
            Vector2 noteXY = new Vector2(note.transform.position.x, note.transform.position.y);
            float d = DistPointToSegment(noteXY, from, to, out Vector2 closest);
            if (d <= hitRange)
            {
                pending[note] = new Pending
                {
                    hitPoint = new Vector3(closest.x, closest.y, note.transform.position.z),
                    velocity = saber.Velocity
                };
            }
        }

        return CheckExits();
    }

    private int CheckExits()
    {
        if (pending.Count == 0) return 0;
        Vector2 now = new Vector2(saber.CurrentPosition.x, saber.CurrentPosition.y);
        float hitRange = bladeRadius + noteHitRadiusXY;

        var toRemove = new List<CuttableNote>();
        int cuts = 0;
        foreach (var kv in pending)
        {
            CuttableNote note = kv.Key;
            if (note == null || note.IsCut || note.IsMissed)
            {
                toRemove.Add(note);
                continue;
            }
            Vector2 noteXY = new Vector2(note.transform.position.x, note.transform.position.y);
            float distNow = Vector2.Distance(now, noteXY);
            if (distNow > hitRange)
            {
                CutDirection imuHint = ResolveImuHint();
                note.Cut(kv.Value.hitPoint, kv.Value.velocity, imuHint);
                cuts++;
                toRemove.Add(note);
            }
        }
        foreach (var n in toRemove) pending.Remove(n);
        return cuts;
    }

    private static bool IsCandidate(CuttableNote n)
    {
        if (n == null) return false;
        if (n.IsCut || n.IsMissed) return false;
        if (!n.IsJudgeable) return false;
        if (!n.gameObject.activeInHierarchy) return false;
        return true;
    }

    // 直近 N 秒以内の IMU 振り検知方向を取得。古ければ None。
    private CutDirection ResolveImuHint()
    {
        if (!Swing8DirectionLogger.TryGetLatest(out CutDirection dir, out float t)) return CutDirection.None;
        if (Time.time - t > imuHintMaxAgeSeconds) return CutDirection.None;
        return dir;
    }

    // 点 p と線分 a→b の最短距離と、線分上の最近点。
    public static float DistPointToSegment(Vector2 p, Vector2 a, Vector2 b, out Vector2 closest)
    {
        Vector2 ab = b - a;
        float denom = ab.sqrMagnitude;
        if (denom < 0.0001f)
        {
            closest = a;
            return Vector2.Distance(p, a);
        }
        float t = Vector2.Dot(p - a, ab) / denom;
        t = Mathf.Clamp01(t);
        closest = a + ab * t;
        return Vector2.Distance(p, closest);
    }
}
