using System;
using UnityEngine;

// 隠し譜面の表。選曲の中央のジャケットを10回切ると、その曲だけの裏の譜面へ移る。
// RUSH E の E(2026-10-14)と、ElDorado の G(2026-10-24 ユーザー「ElDorado のジャケットを10回切ったら、E と同じく裏譜面に」。
// 長い版(3分43秒)の Hard 譜面を全部金色にしたもの。音源・演出・音量そろえも G 専用の audio_g.mp3・stage_g.json・loudness_g.json)と、
// Phobia PARTⅠ の 避(2026-10-24、照準でトゲをよけて的を狙う。譜面は chart_避.json の aim、音源・演出は曲の共通のもの)と、
// Morning の M(2026-10-25、見本を覚え、ノイズの中で同じ2小節を切る。chart_m.json の memory と notes。最初は勉強に作ったが、
// ユーザー「BPMが変わんない曲にしてほしい」で 118BPM 一定の Morning へ移した)と、
// 青葉騒の W(2026-10-25、風に流されて来る。chart_w.json の notes に風の値を持たせる)。
// 色はその譜面の目印(難易度の札・盤の縁・背景・幕・本編の HUD とカウントイン)。E は今までの紫のまま。
public sealed class SecretChart
{
    public string SongId, Difficulty;
    // 目印の色、札の光と面、幕の四角の暗い色と明るい色、背景の地・左右の光・縁・扇の光・中央と左右のもや。
    // 表の中でだけ作る(作ったあとは書き換えない)。
    public Color Accent, Glow, Face, TileDeep, TileLight, Backdrop, LeftLight, RightLight, EdgeLight, FanLight, CenterHaze, LeftHaze, RightHaze;
}

// この選曲中だけの秘密。セーブデータや他の曲へ解除状態を持ち越さない。選んでいる曲に隠し譜面がある(Current)ときだけ数える。
public sealed class RushESecretState
{
    public const int RequiredHits = 10;
    public const float RevealSeconds = .82f;
    public const float TransitionSeconds = 1.85f;
    // 前の切断からこの時間あいたら1回目から数え直す。曲の上で迷う間の偶然の横切りを貯めて裏譜面を出さない。
    public const float ResetSeconds = 3f;
    public static readonly Color Purple = new Color(.74f, .34f, 1f);
    public static readonly SecretChart RushE = new SecretChart
    {
        SongId = "RushE", Difficulty = "E", Accent = Purple, Glow = new Color(.62f, .17f, 1, .25f), Face = new Color(.12f, .03f, .24f),
        TileDeep = new Color(.12f, .025f, .25f), TileLight = new Color(.48f, .17f, .70f),
        Backdrop = new Color(.07f, .005f, .14f), LeftLight = new Color(.65f, .13f, 1), RightLight = new Color(.88f, .23f, 1),
        EdgeLight = new Color(.77f, .45f, 1), FanLight = new Color(.77f, .42f, 1),
        CenterHaze = new Color(.58f, .08f, 1, .42f), LeftHaze = new Color(.6f, .02f, 1, .35f), RightHaze = new Color(.8f, .1f, 1, .35f),
    };
    public static readonly SecretChart ElDoradoG = new SecretChart
    {
        SongId = "ElDorado", Difficulty = "G", Accent = UISkinPalette.NoteGold, Glow = new Color(1, .72f, .15f, .25f), Face = new Color(.22f, .13f, .02f),
        TileDeep = new Color(.24f, .13f, .015f), TileLight = new Color(.80f, .56f, .12f),
        Backdrop = new Color(.10f, .055f, .005f), LeftLight = new Color(1, .60f, .10f), RightLight = new Color(1, .80f, .28f),
        EdgeLight = new Color(1, .88f, .52f), FanLight = new Color(1, .86f, .45f),
        CenterHaze = new Color(1, .62f, .08f, .42f), LeftHaze = new Color(1, .50f, .02f, .35f), RightHaze = new Color(1, .72f, .15f, .35f),
    };
    // Phobia PARTⅠ の「避」(2026-10-24 ユーザー「24 のトゲと 25 のダーツで融合して」「難易度は避」)。
    // 剣が2つの照準になり、トゲをよけて的(ダーツ)の真ん中を狙う(AimDodgeMode)。色はトゲの縁と同じ赤紫。
    public static readonly Color Magenta = new Color(1f, .36f, .84f);
    public static readonly SecretChart PhobiaAvoid = new SecretChart
    {
        SongId = "Phobia PARTⅠ", Difficulty = "避", Accent = Magenta, Glow = new Color(1, .28f, .78f, .25f), Face = new Color(.20f, .03f, .15f),
        TileDeep = new Color(.19f, .02f, .14f), TileLight = new Color(.62f, .16f, .50f),
        Backdrop = new Color(.10f, .01f, .08f), LeftLight = new Color(1, .25f, .70f), RightLight = new Color(1, .45f, .90f),
        EdgeLight = new Color(1, .62f, .92f), FanLight = new Color(1, .56f, .90f),
        CenterHaze = new Color(1, .15f, .65f, .42f), LeftHaze = new Color(.95f, .10f, .60f, .35f), RightHaze = new Color(1, .30f, .80f, .35f),
    };
    public static readonly Color Amber = new Color(1f, .63f, .24f);
    public static readonly SecretChart MorningMemory = new SecretChart
    {
        SongId = "Morning", Difficulty = "M", Accent = Amber, Glow = new Color(1, .55f, .14f, .25f), Face = new Color(.20f, .09f, .025f),
        TileDeep = new Color(.19f, .075f, .015f), TileLight = new Color(.72f, .36f, .10f),
        Backdrop = new Color(.09f, .035f, .008f), LeftLight = new Color(1, .46f, .12f), RightLight = new Color(1, .72f, .32f),
        EdgeLight = new Color(1, .78f, .48f), FanLight = new Color(1, .68f, .34f),
        CenterHaze = new Color(1, .48f, .10f, .42f), LeftHaze = new Color(.95f, .36f, .06f, .35f), RightHaze = new Color(1, .62f, .20f, .35f),
    };
    public static readonly Color Leaf = new Color(.66f, .92f, .30f);
    public static readonly SecretChart AobasouWind = new SecretChart
    {
        SongId = "Aobasou", Difficulty = "W", Accent = Leaf, Glow = new Color(.62f, .90f, .20f, .25f), Face = new Color(.10f, .16f, .025f),
        TileDeep = new Color(.075f, .14f, .015f), TileLight = new Color(.38f, .64f, .12f),
        Backdrop = new Color(.035f, .075f, .008f), LeftLight = new Color(.52f, .86f, .18f), RightLight = new Color(.78f, 1, .38f),
        EdgeLight = new Color(.82f, 1, .54f), FanLight = new Color(.72f, .96f, .40f),
        CenterHaze = new Color(.58f, .88f, .12f, .42f), LeftHaze = new Color(.44f, .78f, .08f, .35f), RightHaze = new Color(.72f, .96f, .24f, .35f),
    };
    public static readonly SecretChart[] Charts = { RushE, ElDoradoG, PhobiaAvoid, MorningMemory, AobasouWind };
    public static SecretChart For(string songId)
    {
        foreach (var chart in Charts) if (chart.SongId == songId) return chart;
        return null;
    }
    public static SecretChart ForDifficulty(string difficulty)
    {
        if (string.IsNullOrWhiteSpace(difficulty)) return null;
        foreach (var chart in Charts) if (string.Equals(chart.Difficulty, difficulty.Trim(), StringComparison.OrdinalIgnoreCase)) return chart;
        return null;
    }
    public static bool IsSecretDifficulty(string difficulty) => ForDifficulty(difficulty) != null;

    public string SelectedSong { get; private set; }
    // 選んでいる曲の隠し譜面(無ければ null)。
    public SecretChart Current { get; private set; }
    public int Hits { get; private set; }
    public bool Transitioning { get; private set; }
    public bool Unlocked { get; private set; }
    double lastHit = double.NegativeInfinity;
    public void Select(string song)
    {
        if (SelectedSong == song) return;
        SelectedSong = song; Current = For(song); Reset();
    }
    public void Reset() { Hits = 0; Transitioning = Unlocked = false; lastHit = double.NegativeInfinity; }
    public bool TryHit(double now)
    {
        if (Current == null || Unlocked || Transitioning || double.IsNaN(now) || double.IsInfinity(now)
            || now - lastHit < .12) return false;
        if (now - lastHit >= ResetSeconds) Hits = 0;
        lastHit = now; Hits++;
        if (Hits == RequiredHits) Transitioning = true;
        return true;
    }
    public bool Reveal()
    {
        if (!Transitioning || Unlocked) return false;
        Unlocked = true; return true;
    }
    public bool Finish()
    {
        if (!Transitioning || !Unlocked) return false;
        Transitioning = false; return true;
    }
    // 判定調整との往復で選曲へ戻ったとき(GameSession.RushESecretCarry、2026-10-22)、解除したままの状態へ戻す。演出はくり返さない。
    // 選んでいる曲に隠し譜面が無ければ何もしない。
    public void RestoreUnlocked()
    {
        if (Current == null) return;
        Hits = RequiredHits; Transitioning = false; Unlocked = true; lastHit = double.NegativeInfinity;
    }
}

// 描画フレーム間で同じカメラ座標を読み直しても、軌跡の時刻や始点を更新しない。
// 受信が途切れたときは始点を捨て、再接続の座標飛びを切断と数えない。
public sealed class RushECameraStroke
{
    readonly RushEJacketStroke stroke = new RushEJacketStroke();
    double lastSample = double.NegativeInfinity;
    public void Reset() { lastSample = double.NegativeInfinity; stroke.Reset(); }
    public bool Tick(Vector2 point, double sampleTime, bool available)
    {
        if (!available || double.IsNaN(sampleTime) || double.IsInfinity(sampleTime)) { Reset(); return false; }
        if (sampleTime == lastSample) return false;
        if (sampleTime < lastSample) stroke.Reset();
        lastSample = sampleTime;
        return stroke.Tick(point, sampleTime, true);
    }
}

// ジャケット半径を1とした座標。細かな手ぶれや滞在を数えず、横切る1振りだけを受け付ける。
public sealed class RushEJacketStroke
{
    Vector2 previous, entry;
    double previousTime;
    bool valid, crossing, spent;
    public void Reset() { valid = crossing = spent = false; }
    public bool Tick(Vector2 point, double now, bool available)
    {
        if (!available || float.IsNaN(point.x) || float.IsNaN(point.y) || float.IsInfinity(point.x) || float.IsInfinity(point.y)
            || double.IsNaN(now) || double.IsInfinity(now)) { Reset(); return false; }
        double dt = now - previousTime;
        if (!valid || dt <= 0 || dt > .18)
        { previous = entry = point; previousTime = now; valid = true; crossing = spent = false; return false; }
        Vector2 delta = point - previous;
        float along = delta.sqrMagnitude > .000001f ? Mathf.Clamp01(-Vector2.Dot(previous, delta) / delta.sqrMagnitude) : 0;
        bool intersects = (previous + delta * along).sqrMagnitude < .81f;
        if (!spent && intersects && !crossing) { crossing = true; entry = previous; }
        bool cut = !spent && crossing && intersects && (point - entry).magnitude >= 1.15f && delta.magnitude / dt >= 2.4;
        if (cut) spent = true;
        if (point.sqrMagnitude > 1.21f) { crossing = false; spent = false; }
        previous = point; previousTime = now;
        return cut;
    }
}
