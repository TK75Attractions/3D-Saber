using System;
using static SfxLab;

// Manic Sonic の裏譜面 R の効果音(ReverseSfx が曲の時計で予約して鳴らす)。どれも鳴らす大きさ(ピーク dBFS)まで作り込んであるので音量 1。
// 部品は派手さカタログと同じ(シンバル・鐘・シャキン・パリーン・シャッ・立ち上がるノイズ・トッ・ドン)。
public static partial class FlourishSfx
{
    // 逆シンバルの長さ。短い逆シンバル(180 BPM の2拍 0.667 秒など)は、頭を飛ばして鳴らす(終わりの頂点は同じ)。
    public const double ReverseCymbalSeconds = 1.0;

    // 逆シンバル: シンバルを逆向きに並べ、届く時刻でいちばん大きくなって、ぷつっと切れる(終わりは 4ms で切る)。
    public static float[] ReverseCymbal(double seconds = ReverseCymbalSeconds)
    {
        double sec = Math.Max(.2, seconds);
        var cymbal = Cymbal(sec, sec * .5, 211);
        Array.Reverse(cymbal);
        Reseed(212);
        var air = Env(Bp(Noise(sec), t => 1200 + 6000 * Math.Pow(t / sec, 2), .9), Swell(sec, 2.6, .004));
        var mixed = Mix(sec, new Layer(Normalize(cymbal, 1)), new Layer(Normalize(air, 1), 0, -9));
        return Finish(mixed, -14, .004);
    }

    // 鏡門の予告音「ピン」: 高いガラスの鐘(約 1.76 kHz・70ms)。同じ音を3回、3つ目の次の拍で門が裏返る。
    public static float[] MirrorCue() => Finish(Bell(.09, 1760, .028, .3), -12, .01);

    // 門が鏡に裏返る「シャン」。
    public static float[] MirrorFlipIn() => Finish(Mix(.45, new Layer(Shing(.4, 1.0, 221)),
        new Layer(Bell(.4, 3520, .12, .2), .01, -10)), -10);

    // 素通しに戻す低い「フォッ」(シャッを下向きに)。
    public static float[] MirrorFlipOut() => Finish(Swish(.26, 2400, 520, 222), -15);

    // 門が割れる「パリーン」。
    public static float[] MirrorShatter() => Finish(Shatter(.6, 223), -9);

    // 世界のきしみ: 低い木と金属(トッと、低く下がるシャッ)。
    public static float[] WorldCreak() => Finish(Mix(.32, new Layer(Thock(.12, 224)),
        new Layer(Swish(.28, 520, 240, 225), .01, -4)), -14);

    // 世界が回る間の、低いうなりのせり上がり(低域の立ち上がるノイズと、小さいスピーカーでも聞こえる倍音の上がる音)。
    public static float[] WorldRumble(double seconds)
    {
        double sec = Math.Max(.5, seconds);
        var noise = SwellNoise(sec, 226, .25);
        var drone = Env(Lp(Osc(sec, t => 110 * Math.Pow(2, t / sec), Wave.Saw), 900, .8), Swell(sec, 1.8, .05));
        return Finish(Mix(sec, new Layer(noise), new Layer(Normalize(drone, 1), 0, -8)), -15);
    }

    // 回り終わりの小さなドン(着地)。
    public static float[] WorldLanding() => Finish(Don(.45, 227), -11);

    // 最後の一回転: 逆シンバルのせり上がり(回る間)と、着いたときのシャキンとシンバルの大きな一打。
    public static float[] WorldFinaleSwell(double seconds) => ReverseCymbal(seconds);

    public static float[] WorldFinaleHit() => Finish(Mix(1.9, new Layer(Shing(.5, 1.0, 228)),
        new Layer(Cymbal(1.8, .6, 229), 0, -4), new Layer(Don(.6, 230), 0, -4)), -5);
}
