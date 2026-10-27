using System;
using UnityEngine;

// 隠し譜面の表。選曲の中央のジャケットを10回切ると、その曲だけの裏の譜面へ移る。
// RUSH E の E(2026-10-14)と、ElDorado の G(2026-10-24 ユーザー「ElDorado のジャケットを10回切ったら、E と同じく裏譜面に」。
// 長い版(3分43秒)の Hard 譜面を全部金色にしたもの。音源・演出・音量そろえも G 専用の audio_g.mp3・stage_g.json・loudness_g.json)と、
// Phobia PARTⅠ の D(2026-10-24 に「避」として作成、10/26 に表記を D へ。照準でトゲをよけて的を狙う。譜面は chart_d.json の aim、
// 音源・演出は曲の共通のもの)と、
// Morning の M(2026-10-25、見本を覚え、ノイズの中で同じ2小節を切る。chart_m.json の memory と notes。最初は勉強に作ったが、
// ユーザー「BPMが変わんない曲にしてほしい」で 118BPM 一定の Morning へ移した)と、
// 青葉騒の W(2026-10-25、風に流されて来る。chart_w.json の notes に風の値を持たせる)と、
// logic!!! の S(2026-10-26、緩急。chart_s.json の scroll でノーツの流れる速さを変える)と、
// Zero の V(2026-10-26、消える・直前に出る。chart_v.json の notes の vanish)と、
// アンダルシアの T(2026-10-26、闘牛。突進と弾に触れた剣は2秒しびれる。chart_t.json の bull)。
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
    // Phobia PARTⅠ の「D」(2026-10-24 ユーザー「24 のトゲと 25 のダーツで融合して」「難易度は避」で作り、
    // 10/26 ユーザー「避譜面の難易度表記をDに変えて」)。剣が2つの照準になり、トゲをよけて的の中へ照準を入れる(AimDodgeMode)。
    // 色はトゲの縁と同じ赤紫。
    public static readonly Color Magenta = new Color(1f, .36f, .84f);
    public static readonly SecretChart PhobiaAvoid = new SecretChart
    {
        SongId = "Phobia PARTⅠ", Difficulty = "D", Accent = Magenta, Glow = new Color(1, .28f, .78f, .25f), Face = new Color(.20f, .03f, .15f),
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
    // logic!!! の「S」(2026-10-26 ユーザー「緩急でいったん作ってそこからどんどん譜面とかギミックを改善して」「それはlogicの裏にして」)。
    // ノーツと小節線が空中で止まったり、走ったり、ゆっくりになったりする(chart_s.json の scroll。届く時刻は音楽どおり)。色は氷の水色。
    public static readonly Color Ice = new Color(.50f, .89f, 1f);
    public static readonly SecretChart LogicSpeed = new SecretChart
    {
        SongId = "logic", Difficulty = "S", Accent = Ice, Glow = new Color(.38f, .84f, 1, .25f), Face = new Color(.025f, .13f, .20f),
        TileDeep = new Color(.015f, .11f, .19f), TileLight = new Color(.14f, .52f, .72f),
        Backdrop = new Color(.006f, .055f, .095f), LeftLight = new Color(.22f, .70f, 1), RightLight = new Color(.50f, .92f, 1),
        EdgeLight = new Color(.62f, .94f, 1), FanLight = new Color(.50f, .88f, 1),
        CenterHaze = new Color(.16f, .66f, 1, .42f), LeftHaze = new Color(.10f, .52f, .95f, .35f), RightHaze = new Color(.30f, .82f, 1, .35f),
    };
    // Zero -狂奏曲-(曲フォルダ「製作中」)の「V」(2026-10-26 ユーザー「zeroはU11の消える案にして」)。
    // ノーツが消えたり、直前に急に出たりする(chart_v.json の notes の vanish)。色は幽霊の薄紫。
    public static readonly Color Phantom = new Color(.86f, .80f, 1f);
    public static readonly SecretChart ZeroVanish = new SecretChart
    {
        SongId = "製作中", Difficulty = "V", Accent = Phantom, Glow = new Color(.78f, .70f, 1, .25f), Face = new Color(.12f, .10f, .20f),
        TileDeep = new Color(.10f, .08f, .18f), TileLight = new Color(.52f, .46f, .70f),
        Backdrop = new Color(.045f, .035f, .085f), LeftLight = new Color(.70f, .62f, 1), RightLight = new Color(.90f, .84f, 1),
        EdgeLight = new Color(.92f, .88f, 1), FanLight = new Color(.84f, .78f, 1),
        CenterHaze = new Color(.66f, .56f, 1, .42f), LeftHaze = new Color(.56f, .46f, .95f, .35f), RightHaze = new Color(.80f, .72f, 1, .35f),
    };
    // アンダルシアの「T」(2026-10-26 ユーザー「アンダルシアは裏譜面Tで、まあただ突進だけじゃなくてシンプル弾幕もちょっと飛んできて、
    // 食らうとその棒が2秒スタンしてしまう感じで」)。ノーツに加えて突進と弾(chart_t.json の bull)。色は闘牛の赤橙。
    public static readonly Color Toro = new Color(1f, .45f, .2f);
    public static readonly SecretChart AndalusiaBull = new SecretChart
    {
        SongId = "Andalusia", Difficulty = "T", Accent = Toro, Glow = new Color(1, .38f, .12f, .25f), Face = new Color(.22f, .07f, .025f),
        TileDeep = new Color(.21f, .055f, .015f), TileLight = new Color(.76f, .28f, .10f),
        Backdrop = new Color(.10f, .025f, .008f), LeftLight = new Color(1, .32f, .10f), RightLight = new Color(1, .56f, .26f),
        EdgeLight = new Color(1, .66f, .44f), FanLight = new Color(1, .56f, .30f),
        CenterHaze = new Color(1, .30f, .06f, .42f), LeftHaze = new Color(.95f, .22f, .04f, .35f), RightHaze = new Color(1, .46f, .14f, .35f),
    };
    // 2:23 AM の「L」(Light、U13 闇と灯り)。二本の刃で夜を照らす。目印は暖かい懐中電灯の色。
    public static readonly Color Lamp = new Color(1f, .86f, .5f);
    public static readonly SecretChart TwoTwentyThreeLight = new SecretChart
    {
        SongId = "2_23_AM", Difficulty = "Y", Accent = Lamp,
        Glow = new Color(1, .82f, .40f, .25f), Face = new Color(.18f, .13f, .045f),
        TileDeep = new Color(.16f, .11f, .025f), TileLight = new Color(.72f, .57f, .22f),
        Backdrop = new Color(.035f, .035f, .055f),
        LeftLight = new Color(1, .76f, .36f), RightLight = new Color(1, .90f, .60f),
        EdgeLight = new Color(1, .91f, .66f), FanLight = new Color(1, .84f, .48f),
        CenterHaze = new Color(1, .78f, .32f, .42f),
        LeftHaze = new Color(.80f, .63f, .26f, .35f), RightHaze = new Color(1, .86f, .48f, .35f),
    };
    // 校歌の F(U66 花吹雪)。Hard と同じノーツが真上から等速で降る。色は淡い桜。
    public static readonly Color Sakura = new Color(1f, .72f, .86f);
    public static readonly SecretChart EpilogueFall = new SecretChart
    {
        SongId = "Epilogue", Difficulty = "F", Accent = Sakura, Glow = new Color(1, .62f, .80f, .25f), Face = new Color(.20f, .08f, .14f),
        TileDeep = new Color(.19f, .065f, .13f), TileLight = new Color(.72f, .38f, .56f),
        Backdrop = new Color(.09f, .025f, .06f), LeftLight = new Color(1, .56f, .75f), RightLight = new Color(1, .78f, .88f),
        EdgeLight = new Color(1, .84f, .92f), FanLight = new Color(1, .74f, .86f),
        CenterHaze = new Color(1, .52f, .73f, .42f), LeftHaze = new Color(.95f, .44f, .66f, .35f), RightHaze = new Color(1, .68f, .82f, .35f),
    };
    // Manic Sonic の X(熱交換)。目印は冷却の青緑、左右の光は熱い銅色と冷たい青緑。
    public static readonly Color Thermal = new Color(.18f, .88f, .68f);
    public static readonly SecretChart ManicSonicHeat = new SecretChart
    {
        SongId = "ManicSonic", Difficulty = "X", Accent = Thermal,
        Glow = new Color(.12f, .88f, .66f, .25f), Face = new Color(.025f, .16f, .12f),
        TileDeep = new Color(.015f, .13f, .10f), TileLight = new Color(.08f, .60f, .44f),
        Backdrop = new Color(.008f, .055f, .045f),
        LeftLight = new Color(1f, .34f, .16f), RightLight = new Color(.10f, .94f, .76f),
        EdgeLight = new Color(.42f, 1f, .78f), FanLight = new Color(.24f, .92f, .65f),
        CenterHaze = new Color(.10f, .76f, .55f, .42f),
        LeftHaze = new Color(1f, .28f, .08f, .35f), RightHaze = new Color(.08f, .86f, .66f, .35f),
    };
    // escapade の L(U47 流星)。逃げるロングを追う。目印は鮮やかなコバルト。
    public static readonly Color Cobalt = new Color(.28f, .46f, 1f);
    public static readonly SecretChart EscapadeLong = new SecretChart
    {
        SongId = "escapade", Difficulty = "L", Accent = Cobalt,
        Glow = new Color(.20f, .38f, 1f, .25f), Face = new Color(.025f, .055f, .22f),
        TileDeep = new Color(.015f, .035f, .20f), TileLight = new Color(.12f, .26f, .78f),
        Backdrop = new Color(.006f, .015f, .10f),
        LeftLight = new Color(.18f, .34f, 1f), RightLight = new Color(.40f, .58f, 1f),
        EdgeLight = new Color(.54f, .68f, 1f), FanLight = new Color(.36f, .54f, 1f),
        CenterHaze = new Color(.12f, .28f, 1f, .42f),
        LeftHaze = new Color(.08f, .20f, .95f, .35f), RightHaze = new Color(.28f, .44f, 1f, .35f),
    };
    // 揺籠のP(U72 Pass)。二速の流れが奥で追い越し、着地前にそろう。目印は月夜の銀青。
    public static readonly Color MoonSilver = new Color(.70f, .74f, .82f);
    public static readonly SecretChart YurikagoOvertake = new SecretChart
    {
        SongId = "揺籠", Difficulty = "P", Accent = MoonSilver,
        Glow = new Color(.60f, .68f, .82f, .25f), Face = new Color(.055f, .07f, .12f),
        TileDeep = new Color(.035f, .05f, .10f), TileLight = new Color(.36f, .42f, .56f),
        Backdrop = new Color(.018f, .024f, .06f),
        LeftLight = new Color(.42f, .54f, .76f), RightLight = new Color(.78f, .82f, .92f),
        EdgeLight = new Color(.80f, .85f, .96f), FanLight = new Color(.62f, .72f, .88f),
        CenterHaze = new Color(.42f, .52f, .74f, .42f),
        LeftHaze = new Color(.30f, .42f, .66f, .35f), RightHaze = new Color(.64f, .70f, .84f, .35f),
    };
    // 勉強(Remaster)のQ(2026-10-27、Milthm「Algebra」のクイズ。chart_q.json の quiz と見えないノーツ)。
    // 曲の途中で黒い試験用紙に切り替わり、リズム・置換行列・数える・最後の大問で答える(QuizMode)。
    // 前の C(U12 Chameleon、白いチョークが化ける)を置き換えた。色は C のチョークの一式のまま。
    public static readonly Color Chalk = new Color(.94f, .93f, .86f);
    public static readonly SecretChart BenkyouQuiz = new SecretChart
    {
        SongId = "BenkyouRemaster", Difficulty = "Q", Accent = Chalk,
        Glow = new Color(.94f, .93f, .86f, .25f), Face = new Color(.13f, .13f, .10f),
        TileDeep = new Color(.09f, .09f, .065f), TileLight = new Color(.62f, .61f, .51f),
        Backdrop = new Color(.035f, .035f, .025f),
        LeftLight = new Color(.84f, .83f, .74f), RightLight = new Color(.98f, .97f, .91f),
        EdgeLight = Chalk, FanLight = new Color(.88f, .87f, .78f),
        CenterHaze = new Color(.78f, .77f, .65f, .42f),
        LeftHaze = new Color(.68f, .67f, .55f, .35f), RightHaze = new Color(.90f, .89f, .78f, .35f),
    };
    public static readonly SecretChart[] Charts = { RushE, ElDoradoG, PhobiaAvoid, MorningMemory, AobasouWind, LogicSpeed, ZeroVanish, AndalusiaBull, TwoTwentyThreeLight, EpilogueFall, ManicSonicHeat, EscapadeLong, YurikagoOvertake, BenkyouQuiz };
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
