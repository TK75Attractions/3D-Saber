using System;
using System.Collections.Generic;
using static SfxLab;

// 派手さカタログ(2026-10-20)で試聴した効果音を、同じ式でコードから作る。外部音源は使わない。
// どれも鳴らす大きさ(ピーク dBFS)まで作り込んであるので、音量 1 で鳴らす(ゲームの音量 1 = 0dBFS、曲は -18 LUFS)。
// 案の音はカウントイン(G)にそろえて G の長調で組む。作った波形は種ごとに一度だけ作って使い回す(Cached)。
public static class FlourishSfx
{
    static readonly Dictionary<string, float[]> cache = new Dictionary<string, float[]>();

    // 同じ名前の音は一度だけ合成する。曲中に合成して処理落ちしないよう、使う側は準備の時点で呼ぶ。
    // 起動時に裏のスレッド(PrewarmAll)も同じ表へ書くので、表の読み書きだけを鍵で守る(合成は鍵の外)。
    public static float[] Cached(string key, Func<float[]> build)
    {
        lock (cache) { if (cache.TryGetValue(key, out var ready)) return ready; }
        var samples = build();
        lock (cache)
        {
            if (cache.TryGetValue(key, out var other)) return other;
            cache[key] = samples;
        }
        return samples;
    }

    public static bool TryGetCached(string key, out float[] samples)
    {
        lock (cache) return cache.TryGetValue(key, out samples);
    }

    // 選曲・本編・リザルトで使う音を全部作る。起動直後に裏のスレッドで呼び、画面を開いた瞬間の処理落ちをなくす。
    // 名前は鳴らす側の Cached の名前とそろえる(MenuSfx・ResultSoundtrack など)。
    public static void PrewarmAll()
    {
        Cached("DiscLeft", () => DiscMove(false)); Cached("DiscRight", () => DiscMove(true));
        for (int i = 0; i < 3; i++) { int k = i; Cached("Difficulty" + k, () => Difficulty(k)); }
        Cached("Calibration", Calibration); Cached("ToTitle", ToTitle); Cached("Fire", Fire); Cached("StartHit", StartHit);
        Cached("Charge1", () => Charge(1)); Cached("Charge2", () => Charge(2)); Cached("StartCharge", StartCharge);
        Cached("Curtain", () => CurtainSwish(false)); Cached("CurtainBack", () => CurtainSwish(true));
        for (int s = 1; s <= 10; s++) { int k = s; Cached("TimerTick" + k, () => TimerTick(k)); }
        Cached("TimerChime", TimerChime);
        PrewarmGameplay();
        Cached("ResultCountSwell", () => CountSwell(ResultSoundtrack.CountSeconds)); Cached("ResultCountStop", CountStop);
        for (int level = 0; level <= 4; level++) { int k = level; Cached("ResultRankFinish." + k, () => RankFinish(k)); }
        Cached("ComboImpact", ComboImpact); Cached("DailyRankDen", DailyRankDen);
    }

    // 本編で使う音を、幕が閉じている間(本編の最初のフレーム)にまとめて作る。2曲目からは作り置きを使う。
    // 名前は鳴らす側(ChorusDrop・JudgmentSfx・GoldNoteSfx など)の Cached の名前とそろえる。
    public static void PrewarmGameplay()
    {
        Cached("ChorusDrop", ChorusDrop); Cached("ChorusEnd", ChorusEnd);
        Cached("CrossCut", CrossCut); Cached("CrowdShort", CrowdShort);
        for (int i = 0; i < 4; i++) { int k = i; Cached("Milestone" + k, () => Milestone(k)); }
        for (int i = 0; i < 3; i++) { int k = i; Cached("GoldCut" + k, () => GoldCut(k)); }
        Cached("GoldCoin", GoldCoin); Cached("GoldOmen", GoldOmen);
        for (int i = 0; i < LongStepCount; i++) { int k = i; Cached("LongStep" + k, () => LongStep(k)); }
        Cached("LongFinish", LongFinish); Cached("RankStamp", RankStamp);
        // 曲の終わり(FULL COMBO・ALL PERFECT の幕、TRACK CLEAR)とサビの余韻も、その場で合成すると1コマ引っかかるので作り置く。
        Cached("FullComboChord", () => ProceduralSfx.Chord(false)); Cached("AllPerfectChord", () => ProceduralSfx.Chord(true));
        Cached("ChorusSparkle", () => ProceduralSfx.Sparkle()); Cached("TrackClearRise", () => ProceduralSfx.Rise());
    }

    // ---- 共通の部品 ----

    // 小さいスピーカー向けの「ドン」(響1)。基音は 78Hz 付近のまま、2〜6倍音(150〜470Hz)を厚くして軽く歪ませ、
    // 耳に無い基音を補わせる。2.6kHz の短い「パキッ」で立ち上がりを出す。60Hz 未満の超低音は入れない。
    public static float[] Don(double seconds = .55, uint seedNo = 99)
        => HarmonicThump(seconds, 165, 78, .08, seedNo, -7, -9);

    // 倍音型の打撃。基音を from→to へ glide 秒で下げ、1〜6倍音を高い倍音ほど速く減らす。
    static float[] HarmonicThump(double seconds, double fromHz, double toHz, double glide, uint seedNo, double crackDb, double airDb)
    {
        int n = Len(seconds);
        var y = new float[n];
        double phase = 0;
        (int h, double a)[] harmonics = { (1, .30), (2, .62), (3, .55), (4, .38), (5, .22), (6, .12) };
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate, f = fromHz + (toHz - fromHz) * Math.Min(1, t / glide);
            phase += 2 * Math.PI * f / Rate;
            double body = 0;
            foreach (var (h, a) in harmonics) body += Math.Sin(h * phase) * a * Math.Exp(-t * (6 + h * 1.6));
            y[i] = (float)(body * Math.Min(1, t / .002));
        }
        var core = Sat(Normalize(y, 1), 1.6);
        Reseed(seedNo); var crack = Env(Bp(Noise(.05), 2600, 1.1), AdExp(.0008, .012));
        Reseed(seedNo + 1); var air = Env(Bp(Noise(.12), 320, 1.4), AdExp(.002, .035));
        var mixed = Mix(seconds, new Layer(core), new Layer(Normalize(crack, 1), 0, crackDb), new Layer(Normalize(air, 1), 0, airDb));
        return FadeTail(Normalize(mixed, .9), .02);
    }

    static float[] FadeTail(float[] x, double seconds)
    {
        int length = Len(seconds);
        for (int i = 0; i < length && i < x.Length; i++) x[x.Length - 1 - i] *= i / (float)length;
        return x;
    }

    // 刃の「シャッ」(帯域ノイズを上へ掃く)。
    static float[] Swish(double seconds, double from, double to, uint seedNo)
    {
        Reseed(seedNo);
        var x = Bp(Noise(seconds), t => from + (to - from) * Math.Min(1, t / seconds), 1.3);
        return Env(Normalize(x, 1), t => Math.Min(1, t / .004) * Math.Pow(Math.Max(0, 1 - t / seconds), 1.6));
    }

    // 倍音つきの鐘。
    static float[] Bell(double seconds, double hz, double decay = .5, double bright = .25)
    {
        var y = new float[Len(seconds)];
        for (int i = 0; i < y.Length; i++)
        {
            double t = i / (double)Rate, e = Math.Min(1, t / .003) * Math.Exp(-t / decay);
            y[i] = (float)((Math.Sin(2 * Math.PI * hz * t) + bright * Math.Sin(2 * Math.PI * hz * 2.01 * t)
                + bright * .4 * Math.Sin(2 * Math.PI * hz * 3.02 * t)) * e);
        }
        return y;
    }

    // 金属的なシンバル(非整数倍の正弦+高い帯域のノイズ)。
    static float[] Cymbal(double seconds, double decay, uint seedNo)
    {
        Reseed(seedNo);
        var noise = Env(Normalize(Hp(Hp(Noise(seconds), 4200, .8), 4200, .8), 1), t => Math.Min(1, t / .002) * Math.Exp(-t / decay));
        double[] hz = { 3127, 3988, 4672, 5341, 6113, 7270, 8031 };
        var partials = new (double, double)[hz.Length];
        for (int k = 0; k < hz.Length; k++) partials[k] = (hz[k], .5 / (1 + k * .35));
        var metal = Env(Additive(seconds, partials), t => Math.Min(1, t / .002) * Math.Exp(-t / (decay * .6)));
        return Mix(seconds, new Layer(noise), new Layer(Normalize(metal, 1), 0, -10));
    }

    // 立ち上がるノイズ(逆再生シンバル風)。scale で掃く高さ全体を変える。
    static float[] SwellNoise(double seconds, uint seedNo, double scale = 1)
    {
        Reseed(seedNo);
        var x = Bp(Noise(seconds), t => (900 + 7000 * Math.Pow(t / seconds, 2)) * scale, .9);
        return Env(Normalize(x, 1), Swell(seconds, 2.4, .015));
    }

    // 金管っぽい和音(のこぎり波を2本ずつわずかにずらして重ね、ローパスで丸める)。
    static float[] Brass(double seconds, int[] notes, double cutoff, double attack, double release)
    {
        var layers = new List<Layer>();
        foreach (int m in notes)
            foreach (double d in new[] { -.006, .006 })
                layers.Add(new Layer(Osc(seconds, Midi(m) * (1 + d), Wave.Saw, Math.Abs(Math.Sin(m * 7.1 + d * 900))), 0, -6));
        var x = Biquad(Mix(seconds, layers.ToArray()), Filter.LowPass, t => cutoff * (.55 + .45 * Math.Min(1, t / .08)), .8);
        return Env(x, t => Math.Min(1, t / attack) * (t > seconds - release ? Math.Max(0, (seconds - t) / release) : 1));
    }

    // 合成の歓声(帯域ノイズの束を人ごとに違う揺れで鳴らす)と拍手。録音素材に替えるまでの仮の音。
    static float[] Crowd(double seconds, uint seedNo)
    {
        var layers = new List<Layer>();
        Reseed(seedNo);
        for (int v = 0; v < 18; v++)
        {
            double center = 500 + Math.Abs(Rnd()) * 2200, rate = 3 + Math.Abs(Rnd()) * 6, phase = Math.Abs(Rnd()) * 6.28, start = Math.Abs(Rnd()) * .18;
            var formant = Bp(Bp(Noise(seconds), center, 3.5), center * 1.12, 3);
            layers.Add(new Layer(Env(Normalize(formant, 1), t =>
            {
                double since = t - start;
                if (since < 0) return 0;
                return Math.Min(1, since / .22) * (.55 + .45 * Math.Sin(2 * Math.PI * rate * t + phase))
                    * Math.Exp(-Math.Max(0, t - seconds * .55) / (seconds * .25));
            }), 0, -3));
        }
        for (int k = 0; k < 70; k++)
        {
            double at = .08 + Math.Abs(Rnd()) * (seconds * .8);
            var clap = Env(Bp(Noise(.04), 1400 + Math.Abs(Rnd()) * 900, 1.2), AdExp(.001, .009));
            layers.Add(new Layer(Normalize(clap, 1), at, -10 - Math.Abs(Rnd()) * 6));
        }
        return Mix(seconds, layers.ToArray());
    }

    // ---- 選曲(盤1・盤2・盤3・盤5) ----

    // ためる音(盤2)。G4 から D6 へ(3倍)上げる細い三角波に、1オクターブ上の正弦と上がっていくノイズを重ねる。
    // 1秒の的は1秒で上がりきる。2秒の的(判定調整・タイトルへ)は同じ形を2秒に伸ばす。
    public static float[] Charge(double seconds)
    {
        double sec = Math.Max(.2, seconds);
        var tone = Env(Osc(sec, t => Midi(67) * Math.Pow(3, Math.Pow(t / sec, 1.3)), Wave.Triangle), t => .25 + .75 * Math.Pow(t / sec, 1.4));
        var tone2 = Env(Osc(sec, t => Midi(79) * Math.Pow(3, Math.Pow(t / sec, 1.3))), t => .3 * Math.Pow(t / sec, 2));
        var x = Mix(sec + .02, new Layer(tone, 0, -2), new Layer(tone2, 0, -8), new Layer(Gain(SwellNoise(sec, 33), .35), 0, -6));
        return Finish(x, -15);
    }

    // 中央の盤(スタート)の2秒のためる音(盤2・盤3)。G3 から G5 へ上がるのこぎり波で、1秒の所で一段明るくする。
    public static float[] StartCharge()
    {
        const double sec = 2.0;
        var tone = Lp(Env(Osc(sec, t => Midi(55) * Math.Pow(4, Math.Pow(t / sec, 1.5)), Wave.Saw),
            t => (.2 + .8 * Math.Pow(t / sec, 1.8)) * (t > 1 ? 1.25 : 1)), 2600, .8);
        var x = Mix(sec + .02, new Layer(tone, 0, -6), new Layer(SwellNoise(sec, 35), 0, -6), new Layer(Bell(.4, Midi(86), .15, .3), 1.0, -14));
        return Finish(x, -10);
    }

    // ためきった瞬間の音(盤2。2026-10-20 ユーザー指摘「溜め後の音が他と同じすぎる」で作り直し)。
    // ためる音の最後の高さ(D6)から1オクターブ上へ跳ねる FM の「キュイッ」、留め金のような2つの「カチッ」、細かい粒のきらめき。
    // ほかの決めの音(シャッ+ドン+鐘・金管)と混ざらないよう、低い打撃も帯域ノイズの掃きも入れない。
    public static float[] Fire()
    {
        const double sec = .45;
        int n = Len(sec);
        var chirp = new float[n];
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate, k = Math.Min(1, t / .045);
            double carrier = Midi(86) * Math.Pow(2, 1 - (1 - k) * (1 - k));
            phase += 2 * Math.PI * carrier / Rate;
            double index = 2.2 * Math.Exp(-t / .03);
            chirp[i] = (float)(Math.Sin(phase + index * Math.Sin(phase * 1.5)) * Math.Min(1, t / .002) * Math.Exp(-t / .07));
        }
        Reseed(141); var click1 = Env(Bp(Noise(.012), 3800, 2.2), AdExp(.0004, .003));
        Reseed(142); var click2 = Env(Bp(Noise(.012), 2200, 2.2), AdExp(.0004, .004));
        var layers = new List<Layer> { new Layer(Normalize(chirp, 1)), new Layer(Normalize(click1, 1), 0, -6), new Layer(Normalize(click2, 1), .018, -9) };
        int[] grains = { 91, 95, 98, 103, 98, 107 };
        Reseed(143);
        for (int g = 0; g < grains.Length; g++)
            layers.Add(new Layer(Env(Osc(.03, Midi(grains[g])), AdExp(.001, .008)), .02 + g * .018 + Math.Abs(Rnd()) * .01, -10 - g * .8));
        return Finish(Mix(sec, layers.ToArray()), -11);
    }

    // スタートの決め(盤3。2026-10-20 ユーザー指摘「溜め後の音が他と同じすぎる」で作り直し)。
    // 青と赤の X 斬り(金属の「シャキン」2本、0.06秒ずらし)→ ジャケットが割れる「パリーン」→ 幕へ向かって上がる
    // G と D の5度の「ギュイーン」。ドン・帯域ノイズの「シャッ」・金管の和音・シンバルは使わない(ほかの決めの音と分ける)。
    public static float[] StartHit()
    {
        var x = Mix(1.4,
            new Layer(Shing(.32, 1.0, 151)), new Layer(Shing(.32, 1.12, 152), .06, -1),
            new Layer(Shatter(.6, 153), .12, -3), new Layer(PowerGlide(.9), .1, -7), new Layer(Thock(.12, 154), .12, -6));
        return Finish(Reverb(x, .9, .16, .4), -4);
    }

    // 金属が鳴る斬撃「シャキン」。短いノイズの擦れに、高い非整数倍の共鳴を少し高さを上げながら重ねる。
    static float[] Shing(double seconds, double scale, uint seedNo)
    {
        Reseed(seedNo);
        var layers = new List<Layer> { new Layer(Normalize(Env(Hp(Noise(.03), 2500, .8), AdExp(.0005, .006)), 1), 0, -6) };
        double[] hz = { 2960, 4410, 5830, 7360 };
        for (int k = 0; k < hz.Length; k++)
        {
            double f = hz[k] * scale, decay = .09 - k * .012;
            layers.Add(new Layer(Env(Osc(seconds, t => f * (1 + .03 * Math.Min(1, t / .08))), t => Math.Min(1, t / .002) * Math.Exp(-t / decay)), 0, -k * 2.5));
        }
        return Normalize(Mix(seconds, layers.ToArray()), 1);
    }

    // ジャケットが割れる「パリーン」。高い非整数倍の粒を、ばらばらの時刻と長さで鳴らす(はじめほど密に)。
    static float[] Shatter(double seconds, uint seedNo)
    {
        Reseed(seedNo);
        var layers = new List<Layer>();
        for (int g = 0; g < 42; g++)
        {
            double at = Math.Pow(Math.Abs(Rnd()), 1.8) * .28, hz = 2200 + Math.Abs(Rnd()) * 7000;
            double decay = .02 + Math.Abs(Rnd()) * .09, db = -4 - Math.Abs(Rnd()) * 10 - at * 12;
            layers.Add(new Layer(Env(Osc(decay * 5, hz), t => Math.Min(1, t / .0008) * Math.Exp(-t / decay)), at, db));
        }
        layers.Add(new Layer(Normalize(Env(Hp(Noise(.18), 3000, .7), t => Math.Min(1, t / .001) * Math.Exp(-t / .035)), 1), 0, -4));
        return Normalize(Mix(seconds, layers.ToArray()), 1);
    }

    // 幕へ向かって上がる5度(G と D)の「ギュイーン」。のこぎり波を2本ずつずらし、フィルターを開きながら G5 から D6 へ上げる。
    static float[] PowerGlide(double seconds)
    {
        var layers = new List<Layer>();
        foreach (double ratio in new[] { 1.0, 1.4983 })
            foreach (double d in new[] { -.007, .007 })
                layers.Add(new Layer(Osc(seconds, t => Midi(79) * ratio * (1 + d) * Math.Pow(2, 7.0 / 12 * Math.Min(1, Math.Pow(t / .45, .7))), Wave.Saw), 0, -6));
        var x = Biquad(Mix(seconds, layers.ToArray()), Filter.LowPass, t => 900 + 5200 * Math.Min(1, t / .4), 1.1);
        return Normalize(Env(x, t => Math.Min(1, t / .03) * Math.Exp(-Math.Max(0, t - .3) / .25)), 1);
    }

    // 小さいスピーカーでも出る中域の「トッ」(200〜400Hz)。ドンのような長い低音にはしない。
    static float[] Thock(double seconds, uint seedNo)
    {
        Reseed(seedNo);
        var body = Env(Osc(seconds, t => 260 - 80 * Math.Min(1, t / .05)), AdExp(.001, .03));
        var knock = Env(Bp(Noise(seconds), 420, 1.6), AdExp(.0005, .018));
        return Normalize(Mix(seconds, new Layer(body), new Layer(Normalize(knock, 1), 0, -4)), 1);
    }

    // 曲送り(盤1): 「ヒュッ」+「カチッ」。左は低め、右は高め。
    public static float[] DiscMove(bool right) => right
        ? Finish(Mix(.25, new Layer(Swish(.09, 1000, 3600, 10), 0, -2), new Layer(Env(Osc(.04, Midi(86), Wave.Triangle), AdExp(.0005, .012)), .06, -6)), -10)
        : Finish(Mix(.25, new Layer(Swish(.09, 700, 2600, 9), 0, -2), new Layer(Env(Osc(.04, Midi(79), Wave.Triangle), AdExp(.0005, .012)), .06, -6)), -10);

    // 難易度(盤1): EASY は G5+B5、NORMAL は B5+D6、HARD は D6+G6 に画面のノイズと同じ「ジジッ」。
    public static float[] Difficulty(int index)
    {
        if (index <= 0) return Finish(Mix(.4, new Layer(Bell(.4, Midi(79), .12, .2)), new Layer(Bell(.4, Midi(83), .12, .2), .05, -2)), -10);
        if (index == 1) return Finish(Mix(.4, new Layer(Bell(.4, Midi(83), .12, .25)), new Layer(Bell(.4, Midi(86), .12, .25), .05, -2)), -10);
        Reseed(73);
        var buzz = Env(Bp(Noise(.18), 1800, 2), t => (Math.Sin(2 * Math.PI * 38 * t) > 0 ? 1 : .2) * Math.Exp(-t / .07));
        return Finish(Mix(.55, new Layer(Bell(.5, Midi(86), .14, .35)), new Layer(Bell(.5, Midi(91), .14, .35), .05, -2),
            new Layer(Normalize(buzz, 1), .02, -9), new Layer(Don(.35, 71), 0, -12)), -9);
    }

    // 判定調整へ(盤1): 柔らかい上がる2音(D5 → G5)。タイトルへ: 下がる2音(G5 → D5)。
    public static float[] Calibration() => Finish(Mix(.6, new Layer(Bell(.5, Midi(74), .16, .08)), new Layer(Bell(.5, Midi(79), .2, .08), .09, -1)), -11);
    public static float[] ToTitle() => Finish(Mix(.6, new Layer(Bell(.5, Midi(79), .14, .12)), new Layer(Bell(.5, Midi(74), .2, .12), .09, -1)), -11);

    // 残り時間の刻み(盤5)。10〜4秒は D5 から1秒ごとに半音上げ、最後の3秒は G5 で 5dB 大きく(ピーク -9dBFS)。
    public static float[] TimerTick(int secondsLeft)
    {
        int k = 10 - Math.Max(1, Math.Min(10, secondsLeft));
        bool last = k >= 7;
        var loud = TickVoice(79, 5);
        var x = last ? loud : TickVoice(74 + k, 0);
        return Gain(x, DbToGain(-9) / Math.Max(1e-6, Peak(loud)));
    }

    static float[] TickVoice(int note, double db)
        => Mix(.12, new Layer(Env(Osc(.1, Midi(note), Wave.Square), AdExp(.001, .02)), 0, -4 + db),
            new Layer(Env(Osc(.1, Midi(note + 12)), AdExp(.001, .015)), 0, -10 + db));

    // 残り30秒の知らせ(盤5)。D6 と G5 の鐘を続けて鳴らす「ポーン・ポーン」。
    public static float[] TimerChime() => Finish(Mix(1.4, new Layer(Bell(1.1, Midi(86), .45, .18)), new Layer(Bell(1.1, Midi(79), .5, .18), .32, -1)), -12);

    // 画面の幕が閉じる「シュッ」(幕1)。帯域ノイズを 0.9kHz から 7.9kHz へ上げる。戻る幕は 0.8 倍の高さで低く。
    public static float[] CurtainSwish(bool back, double seconds = ScreenCloseSeconds)
        => Finish(SwellNoise(seconds, back ? 38u : 37u, back ? .8 : 1), -12);
    public const double ScreenCloseSeconds = .42;

    // ---- プレイ中(祭2・波1・斬2・斬3・斬5・波3) ----

    // サビの入口(祭2): 倍音型のドンにシンバルと歓声を重ねる。
    public static float[] ChorusDrop() => Finish(Mix(2.4, new Layer(Don(.6, 91)), new Layer(Cymbal(2.0, .6, 25), 0, -6), new Layer(Crowd(2.3, 45), .05, -12)), -4);

    // サビの終わり(祭2): 下がる「シュー」(0.6秒)。
    public static float[] ChorusEnd()
    {
        const double sec = .6;
        Reseed(57);
        var x = Bp(Noise(sec), t => 6500 - 5700 * Math.Pow(t / sec, .6), 1.1);
        return Finish(Env(Normalize(x, 1), t => Math.Min(1, t / .04) * Math.Pow(Math.Max(0, 1 - t / sec), 1.4)), -16);
    }

    // コンボの節目(波1): G の長調の分散和音を、節目ごとに一段上の形から鳴らす(4段で上限)。
    public static float[] Milestone(int step)
    {
        int[][] shapes = { new[] { 79, 83, 86, 91 }, new[] { 83, 86, 91, 95 }, new[] { 86, 91, 95, 98 }, new[] { 91, 95, 98, 103 } };
        int k = Math.Max(0, Math.Min(3, step));
        var layers = new List<Layer>();
        for (int i = 0; i < 4; i++) layers.Add(new Layer(Bell(.9, Midi(shapes[k][i]), .32, .3), i * .045, -i * 1.2));
        layers.Add(new Layer(Gain(Swish(.18, 3000, 9000, (uint)(11 + k)), .5), 0, -10));
        return Finish(Mix(1.0, layers.ToArray()), -8);
    }

    // 100 と 300 の節目に足す短い歓声(波1)。
    public static float[] CrowdShort() => Finish(Crowd(1.4, 47), -14);

    // 左右同時の X 斬り(斬3): 2本の「シャッ」を 30ms ずらし、D の5度の和音を重ねる。
    // カタログの -4dBFS は Perfect の切断音(-24.8 LUFS)より約9dB 大きかったので、-6dBFS にした。
    public static float[] CrossCut() => Finish(Mix(.8,
        new Layer(Swish(.12, 1600, 6000, 13), 0, -1), new Layer(Swish(.12, 2000, 7500, 14), .03, -1),
        new Layer(Brass(.5, new[] { 62, 69, 74 }, 3200, .004, .35), .01, -8), new Layer(Don(.4, 101), 0, -6),
        new Layer(Bell(.6, Midi(98), .25, .3), .03, -10)), -6);

    // 金ノーツの「キィン」(斬2)。Perfect と Great だけで鳴らす。続けて切ると G → B → D と一段ずつ上がる(3段まで)。
    public static float[] GoldCut(int step)
    {
        int k = Math.Max(0, Math.Min(2, step)), up = new[] { 0, 4, 7 }[k];
        return Finish(Mix(.8, new Layer(Swish(.08, 2500, 8000, (uint)(17 + k)), 0, -4), new Layer(Bell(.7, Midi(91 + up), .22, .35), 0, -1),
            new Layer(Bell(.7, Midi(98 + up), .18, .3), .03, -4), new Layer(Bell(.5, Midi(103 + up), .1, .2), .06, -9),
            new Layer(Don(.3, (uint)(121 + k)), 0, -12)), -7);
    }

    // 金ノーツが判定の窓に入る少し前の「チリッ」(斬2)。小さい3粒のきらめき。
    // カタログの -24dBFS は -39 LUFS で曲の下では聞こえないので、-20dBFS にした。
    public static float[] GoldOmen()
    {
        int[] notes = { 98, 103, 107 };
        var layers = new Layer[notes.Length];
        for (int i = 0; i < notes.Length; i++) layers[i] = new Layer(Env(Osc(.05, Midi(notes[i])), AdExp(.0008, .012)), i * .022, -i * 2);
        return Finish(Mix(.12, layers), -20);
    }

    // スコアの所で鳴る「チャリン」(斬2)。硬貨のように高い2音を少しずらす。
    public static float[] GoldCoin()
    {
        Reseed(161);
        var a = Env(Additive(.35, new[] { (2637.0, 1.0), (2637.0 * 2.76, .35), (2637.0 * 5.4, .12) }), AdExp(.001, .05));
        var b = Env(Additive(.45, new[] { (3951.0, 1.0), (3951.0 * 2.76, .3) }), AdExp(.001, .12));
        return Finish(Mix(.5, new Layer(Normalize(a, 1), 0, -2), new Layer(Normalize(b, 1), .055)), -18);
    }

    // ロングの途中の1回(斬5)。G の長調で一段ずつ上がる鐘と小さい「シャッ」。8段目からは G6 のまま。
    static readonly int[] longSteps = { 79, 81, 83, 84, 86, 88, 90, 91 };
    public static int LongStepCount => longSteps.Length;
    public static float[] LongStep(int index)
    {
        int i = Math.Max(0, Math.Min(longSteps.Length - 1, index));
        return Finish(Mix(.2, new Layer(Swish(.05, 2500, 6000, (uint)(19 + i)), 0, -6), new Layer(Bell(.2, Midi(longSteps[i]), .06, .3))), -16);
    }

    // ロングの最後の一振り(斬5)の「チーン」(G6 と D6 の鐘と小さいドン)。
    public static float[] LongFinish() => Finish(Mix(1.0, new Layer(Bell(1.0, Midi(91), .4, .35)), new Layer(Bell(1.0, Midi(86), .45, .35), .02, -3),
        new Layer(Don(.3, 131), 0, -10)), -10);

    // 曲中にランクが上がったときの「ガシャン」(波3)。判子を押す中域の「ガッ」と、遅れて鳴る金属の「シャン」(非整数倍の部分音)。
    public static float[] RankStamp()
    {
        Reseed(171);
        var thud = Mix(.2, new Layer(Env(Osc(.2, t => 210 - 70 * Math.Min(1, t / .04)), AdExp(.001, .035))),
            new Layer(Normalize(Env(Bp(Noise(.2), 650, 1.4), AdExp(.0005, .02)), 1), 0, -3));
        Reseed(172);
        var clang = Env(Additive(.5, new[] { (1180.0, 1.0), (1580.0, .7), (2310.0, .55), (3420.0, .4), (4870.0, .25) }), t => Math.Min(1, t / .001) * Math.Exp(-t / .12));
        Reseed(173);
        var sizzle = Env(Hp(Noise(.25), 5000, .7), AdExp(.001, .05));
        return Finish(Mix(.6, new Layer(Normalize(thud, 1)), new Layer(Normalize(clang, 1), .04, -4), new Layer(Normalize(sizzle, 1), .04, -12)), -9);
    }

    // ---- リザルト(賞2・賞5) ----

    // スコアの数字が上がっている間に下で上がっていく音と、止まった瞬間の G6 の「チン」(賞5)。
    public static float[] CountSwell(double seconds) => Finish(SwellNoise(Math.Max(.2, seconds), 61), -24);
    public static float[] CountStop() => Finish(Bell(.7, Midi(91), .25, .3), -14);

    // ランクの決め(賞2)。level: 4=S+ / 3=S / 2=A / 1=B / 0=C。長さは S+ 約3秒、S 約2秒、A 1.2秒、B・C 0.8秒。
    public static float[] RankFinish(int level)
    {
        switch (level)
        {
            case 4:
                return Finish(Reverb(Mix(2.2, new Layer(Don(.6, 141)), new Layer(Cymbal(1.8, .6, 29), 0, -9),
                    new Layer(Brass(1.6, new[] { 67, 74, 79, 83, 86 }, 3600, .006, .9), .01, -8), new Layer(Bell(1.6, Midi(98), .6, .3), .04, -12),
                    new Layer(Crowd(2.0, 49), .1, -16)), 1.2, .2, .4), -3);
            case 3:
                // 短いファンファーレ: D の和音を短く刻んでから G の和音。
                return Finish(Reverb(Mix(1.4, new Layer(Don(.5, 145), 0, -2), new Layer(Brass(.14, new[] { 62, 66, 69, 74 }, 3400, .004, .06), 0, -2),
                    new Layer(Brass(1.0, new[] { 67, 71, 74, 79 }, 3600, .006, .6), .16), new Layer(Cymbal(1.1, .45, 27), .16, -12)), .8, .18, .4), -6.5);
            case 2:
                return Finish(Mix(1.2, new Layer(Don(.5, 151)), new Layer(Bell(.9, Midi(79), .3, .25), .01, -8), new Layer(Bell(.9, Midi(83), .3, .25), .01, -10)), -4);
            default:
                return Finish(SoftChord(.8, new[] { 67, 71, 74 }), -12);
        }
    }

    // 柔らかい和音(B・C のランク)。正弦に2倍音を少し足し、ゆっくり立ち上げる。
    static float[] SoftChord(double seconds, int[] notes)
    {
        var y = new float[Len(seconds)];
        for (int i = 0; i < y.Length; i++)
        {
            double t = i / (double)Rate, sum = 0;
            for (int n = 0; n < notes.Length; n++)
            {
                double hz = Midi(notes[n]), on = t - n * .03;
                if (on < 0) continue;
                sum += (Math.Sin(2 * Math.PI * hz * on) + .18 * Math.Sin(4 * Math.PI * hz * on)) * Math.Min(1, on / .03) * Math.Exp(-on / .3);
            }
            y[i] = (float)sum;
        }
        return FadeTail(y, .05);
    }

    // ---- 響1 で作り直した、ドンと同じ作りの音 ----

    // 曲の終わりのコンボボーナスの着地音。135→55Hz の低音を倍音型にし(終わりは 62Hz で止めて 60Hz 未満を入れない)、
    // 880Hz と 1320Hz の鐘はそのまま重ねる。ピークは元の音(約 .81)にそろえる。
    public static float[] ComboImpact()
    {
        const double sec = .65;
        var body = HarmonicThump(sec, 135, 62, .17, 181, -9, -10);
        var bell = new float[Len(sec)];
        for (int i = 0; i < bell.Length; i++)
        {
            double t = i / (double)Rate;
            bell[i] = (float)((Math.Sin(2 * Math.PI * 880 * t) + .4 * Math.Sin(2 * Math.PI * 1320 * t)) * Math.Exp(-t * 8) * Math.Min(1, t / .004));
        }
        var x = Mix(sec, new Layer(Normalize(body, 1)), new Layer(Normalize(bell, 1), 0, -10));
        Normalize(x, .81);
        return FadeTail(x, .04);
    }

    // 本日ランキングの1〜3位の「デン」。150→62Hz の低音を倍音型にし、440Hz と 660Hz の音はそのまま。ピークは元の音(約 .7)にそろえる。
    public static float[] DailyRankDen()
    {
        const double sec = .65;
        var body = HarmonicThump(sec, 150, 62, .16, 191, -9, -10);
        var tones = new float[Len(sec)];
        for (int i = 0; i < tones.Length; i++)
        {
            double t = i / (double)Rate, p = i / (double)tones.Length;
            tones[i] = (float)((.12 * Math.Sin(2 * Math.PI * 440 * t) + .06 * Math.Sin(2 * Math.PI * 660 * t)) * Math.Min(1, t / .002) * Math.Pow(1 - p, 2.4));
        }
        var x = Mix(sec, new Layer(Normalize(body, 1)), new Layer(Normalize(tones, 1), 0, -11));
        Normalize(x, .7);
        return FadeTail(x, .04);
    }
}
