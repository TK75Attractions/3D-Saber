using UnityEngine;

// 判定が「ノーツが来た後」にずれる原因を調べるため、1回の判定ごとに時刻の差を events.log に残す
// （2026-10-17 ユーザー: プロジェクターで「来た後に判定される」）。記録だけで、判定・演出・ノーツは変えない。
// judge-ms  : ゲームが Perfect 等を決めるのに使う誤差（剣が通った判定時刻 LastCutSongTime − ノーツの到着時刻、ScoreManager と同じ）。
//             正なら遅め。ロング（複数カット）は完了率で決まるので unknown。
// decided-ms: 判定が確定したフレームの判定の時計 − 到着時刻（10-18 未明までの judge-ms。確定の待ちを含むので補正の目安には使わない）。
// song-ms   : 曲時計（画面の描画と同じ時計）− ノーツの到着時刻。PC が判定した時点で、画面上のノーツがどれだけ過ぎていたか。
// latency-ms: ゲームが差し引いているカメラ遅れの補正値。
public sealed class PhoneSaberJudgeTimingLog : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void CreateAtStartup()
    {
        if (!PhoneSaberP2PBridgeProcess.AutoStartEnabled) return; // テスト中は作らない
        var go = new GameObject("PhoneSaber Judge Timing Log");
        go.AddComponent<PhoneSaberJudgeTimingLog>();
        DontDestroyOnLoad(go);
    }

    GamePlayManager manager;
    NoteSpawner spawner;
    double nextLookup;
    string station;

    public static string Detail(string tier, double hitTime, double songTime, double? cutSongTime, double judgeLatencySeconds, string hand)
    {
        double judgeClock = songTime - judgeLatencySeconds;
        return "tier=" + tier + " hand=" + hand
            + " judge-ms=" + Ms(cutSongTime.HasValue ? cutSongTime.Value - hitTime : double.NaN)
            + " decided-ms=" + Ms(judgeClock - hitTime) + " song-ms=" + Ms(songTime - hitTime)
            + " latency-ms=" + Ms(judgeLatencySeconds) + " hit-s=" + hitTime.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
    }

    static string Ms(double seconds) =>
        double.IsNaN(seconds) || double.IsInfinity(seconds) ? "unknown"
            : System.Math.Round(seconds * 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture);

    // 本編の GamePlayManager はシーンごとに作り直されるので、秒1回だけ探す（見つかったら探さない）。
    void Update()
    {
        if (manager != null) return;
        double now = Time.realtimeSinceStartupAsDouble;
        if (now < nextLookup) return;
        nextLookup = now + 1;
        manager = FindAnyObjectByType<GamePlayManager>();
        if (manager == null || manager.noteSpawner == null) { manager = null; return; }
        Unsubscribe();
        spawner = manager.noteSpawner;
        spawner.OnNoteSpawned += Watch;
        spawner.OnNoteMissed += Missed;
        station = PhoneSaberStation.Read();
    }

    void OnDestroy() => Unsubscribe();

    void Unsubscribe()
    {
        if (spawner == null) return;
        spawner.OnNoteSpawned -= Watch;
        spawner.OnNoteMissed -= Missed;
        spawner = null;
    }

    // ノーツは使い回され、再利用時に購読が消える。生成ごとに付け直す。
    void Watch(CuttableNote note)
    {
        if (note == null) return;
        note.OnJudged -= Judged;
        note.OnJudged += Judged;
    }

    void Judged(CuttableNote note, JudgmentTier tier, Vector3 point, Vector3 velocity) =>
        Record(note, tier.ToString());

    void Missed(CuttableNote note) => Record(note, "Miss");

    void Record(CuttableNote note, string tier)
    {
        if (note == null || manager == null || manager.songPlayer == null || spawner == null) return;
        // ゲームと同じ誤差（タップ／フリックだけ）。ロングと Miss は時刻の誤差で決まらない。
        double? cut = tier != "Miss" && note.RequiredCutCount <= 1 ? note.LastCutSongTime : null;
        if (cut.HasValue) PhoneSaberJudgeTimingStats.AddHit((cut.Value - note.HitTime) * 1000.0);
        PhoneSaberEventLog.Record(station, "SYSTEM", "judge-timing",
            Detail(tier, note.HitTime, manager.songPlayer.SongTime, cut, spawner.JudgeLatencySeconds, note.RequiredHand.ToString()));
    }
}
