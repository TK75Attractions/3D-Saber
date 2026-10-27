using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Manic Sonic の裏譜面「R(反転)」の決まり(純関数。テストは主にここで取る)。
// 時刻の引数は、ミリ秒(ms と付くもの。譜面の time と同じ)か、譜面の時計の秒(曲の時計 − offset)。
//   ・鏡門(U105): 門は届く 0.5 秒前の深さに立ち、そこを通るノーツが本当の位置へ 0.08 秒で滑る(見た目だけ。根元は最初から本当の位置)。
//   ・天地返し(U103): 画面の絵(投影の行列)をノーツの帯の中心のまわりに回し、剣の入力はカメラの経路で逆に回す。
public static class ReverseRules
{
    // ---- 鏡門 ----
    // ノーツが門を通るのは届く 0.5 秒前(門の深さ = judgeZ + 速さ × 0.5)。通ったあと 0.08 秒で本当の位置へ滑る。
    public const double CrossSeconds = .5;
    public const double SlideSeconds = .08;
    // 門の切り替えの前後この秒数には、門を通るノーツを置かない(生成道具とテストで確かめる)。
    public const double SwitchClearSeconds = .15;
    // 門の内側の半分の大きさ(幅 5.2 × 高さ 2.5)と枠の太さ。中心はノーツの帯の中心(BandCenter)。
    public const float GateHalfWidth = 2.6f, GateHalfHeight = 1.25f, GateBar = .07f;
    // 裏返り・割れる破片・F3 で薄くなって消える時間、出てくるときに入る時間。
    public const double FlipSeconds = .33, ShatterSeconds = .6, ReducedFadeSeconds = .3, GateFadeInSeconds = .15;
    // 枠の光(素通しは 40%、鏡は 100%)。
    public const float PassGlow = .4f, MirrorGlow = 1f;
    // 門を通ったときの横切りの筋と、両端の波紋。
    public const double StreakSeconds = .2;
    public const float StreakWidth = .06f, RippleRadius = .35f, StreakAlpha = .35f;
    // 鏡のノーツの床の印は、門を通るまでこの濃さ。
    public const float FloorMarkBeforeCross = .5f;
    // 予告音の数(鏡にする拍の前の3拍)。
    public const int CueCount = 3;
    // 鏡の矢印の拡縮が 0 にならないよう(0 の行列は逆行列が作れない)、押しつぶれる最小の大きさ。
    public const float MinMirrorScale = .04f;

    // ---- 天地返し ----
    // ノーツの帯の中心。回転の軸で、門の中心。
    public static readonly Vector2 BandCenter = new Vector2(0f, -.15f);
    public static Vector2 Pivot => BandCenter;
    public const double CreakSeconds = .25, CreakRiseSeconds = .05;
    public const float MaxCreakDegrees = 15f, MaxTurnDegrees = 3600f;
    // F3 では回る動きを見せず、回転の真ん中で 0.5 秒だけ画面を暗くして(70%)、暗い間に向きを切り替える。
    public const double ReducedDimSeconds = .5, ReducedDimRampSeconds = .1;
    public const float ReducedDimAlpha = .7f;
    // R の実効の接近時間は min(設定, 2.0 秒)。天地返しで回っている間(127.5〜131.5 秒)にノーツが見えないように
    // (133.5 秒のノーツは接近 2.0 秒で回り終わりちょうどに出る)。設定の3段(2.0・1.0・0.8 秒)はそのまま使う。保存されている設定は変えない。
    // 2026-10-28: 下限 1.5 秒(鏡のノーツを門より奥で長く見せるため)は、既定の 1.0 秒が遅くなるのでやめた(ユーザー「理由ないならもとのに戻して」)。
    public const double MaximumApproach = 2.0;
    // 設定が壊れているときの接近時間(GameSession.NoteApproachTimeDefault と同じ)。
    public const double DefaultApproach = 1.0;

    public static bool Enabled(ReverseChartData data) => data != null && data.enabled;

    public static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    static float Ease(double u)
    {
        float k = Mathf.Clamp01((float)u);
        return k * k * (3f - 2f * k);
    }

    // ---- 読み込み ----

    // 区間は時刻順に並べ、長さの無いもの・壊れた値は捨てる。on は門の時間の中へ収める。角度は有限に収める。
    // 門を通る時刻(届く − 0.5 秒)が鏡の区間に入らない mirror は外す(門なしで裏返るのを防ぐ)。ロング・見えないノーツは鏡にしない。
    public static ReverseChartData Normalize(ReverseChartData data, List<NoteData> notes)
    {
        data ??= new ReverseChartData();
        data.gates = (data.gates ?? new List<ReverseGateData>())
            .Where(g => g != null && Finite(g.time) && Finite(g.endTime) && g.endTime > g.time)
            .OrderBy(g => g.time).ToList();
        foreach (var gate in data.gates)
        {
            gate.on = (gate.on ?? new List<ReverseSpanData>())
                .Where(s => s != null && Finite(s.time) && Finite(s.endTime))
                .Select(s => new ReverseSpanData { time = Mathf.Max(s.time, gate.time), endTime = Mathf.Min(s.endTime, gate.endTime) })
                .Where(s => s.endTime > s.time)
                .OrderBy(s => s.time).ToList();
        }
        data.turns = (data.turns ?? new List<ReverseTurnData>())
            .Where(t => t != null && Finite(t.time) && Finite(t.endTime) && Finite(t.degrees))
            .OrderBy(t => t.time).ToList();
        foreach (var turn in data.turns)
        {
            if (turn.endTime < turn.time) turn.endTime = turn.time;
            turn.degrees = Mathf.Clamp(turn.degrees, -MaxTurnDegrees, MaxTurnDegrees);
        }
        data.creaks = (data.creaks ?? new List<ReverseCreakData>())
            .Where(c => c != null && Finite(c.time) && Finite(c.degrees))
            .OrderBy(c => c.time).ToList();
        foreach (var creak in data.creaks) creak.degrees = Mathf.Clamp(creak.degrees, -MaxCreakDegrees, MaxCreakDegrees);
        if (notes != null)
            foreach (var note in notes)
                if (note != null && note.mirror && (!data.enabled || note.IsLong || note.hidden
                    || !MirrorOn(data, note.time - CrossSeconds * 1000.0)))
                    note.mirror = false;
        return data;
    }

    // R の実効の接近時間(秒)。プレイヤーの保存値は変えず、R の流れる速さだけに使う。
    public static double EffectiveApproach(double requested)
        => Finite(requested) && requested > 0 ? Math.Min(MaximumApproach, requested) : DefaultApproach;

    // ---- 鏡門 ----

    public static double CrossTime(double hitSeconds) => hitSeconds - CrossSeconds;

    // 門を通ってからの滑りの進み(0 = 鏡像の側、1 = 本当の位置)。残り時間だけで決まるので、コマ飛びで門を飛び越しても正しい位置に出る。
    public static float MirrorBlend(double remaining) => Finite(remaining) ? Ease((CrossSeconds - remaining) / SlideSeconds) : 1f;

    // 見た目の子の横のずれ(ワールド)。本当の x の鏡像(−x)に見せてから、本当の x へ。
    public static float MirrorOffsetX(float landingX, double remaining) => -2f * landingX * (1f - MirrorBlend(remaining));

    // 見た目の子の横の拡縮。−1(鏡。矢印も鏡)から +1 へ、真ん中で押しつぶれて戻る(ノーツ自身も裏返って見える)。
    public static float MirrorScaleX(double remaining)
    {
        float s = Mathf.Lerp(-1f, 1f, MirrorBlend(remaining));
        return Mathf.Abs(s) < MinMirrorScale ? (s < 0f ? -MinMirrorScale : MinMirrorScale) : s;
    }

    // 左右の鏡の矢印(横の成分を反転。上下は変えない)。
    public static CutDirection MirrorDirection(CutDirection direction)
    {
        switch (direction)
        {
            case CutDirection.Left: return CutDirection.Right;
            case CutDirection.Right: return CutDirection.Left;
            case CutDirection.UpLeft: return CutDirection.UpRight;
            case CutDirection.UpRight: return CutDirection.UpLeft;
            case CutDirection.DownLeft: return CutDirection.DownRight;
            case CutDirection.DownRight: return CutDirection.DownLeft;
            default: return direction;
        }
    }

    // 180° 回した矢印(逆向き)。逆さの間のノーツは、画面で見えてほしい向きの逆を譜面に焼く。
    public static CutDirection TurnDirection(CutDirection direction)
    {
        switch (direction)
        {
            case CutDirection.Up: return CutDirection.Down;
            case CutDirection.Down: return CutDirection.Up;
            case CutDirection.Left: return CutDirection.Right;
            case CutDirection.Right: return CutDirection.Left;
            case CutDirection.UpLeft: return CutDirection.DownRight;
            case CutDirection.DownRight: return CutDirection.UpLeft;
            case CutDirection.UpRight: return CutDirection.DownLeft;
            case CutDirection.DownLeft: return CutDirection.UpRight;
            default: return direction;
        }
    }

    // 譜面の direction の文字列(none・up・downleft …)のまま写す。
    public static string MirrorDirection(string direction) => DirectionName(MirrorDirection(CutDirectionHelper.Parse(direction)));
    public static string TurnDirection(string direction) => DirectionName(TurnDirection(CutDirectionHelper.Parse(direction)));

    public static string DirectionName(CutDirection direction)
    {
        switch (direction)
        {
            case CutDirection.Up: return "up";
            case CutDirection.Down: return "down";
            case CutDirection.Left: return "left";
            case CutDirection.Right: return "right";
            case CutDirection.UpLeft: return "upleft";
            case CutDirection.UpRight: return "upright";
            case CutDirection.DownLeft: return "downleft";
            case CutDirection.DownRight: return "downright";
            default: return "none";
        }
    }

    // ms の時刻が、どれかの門の鏡の区間 [on.time, on.endTime) に入っているか。
    public static bool MirrorOn(ReverseChartData data, double ms)
    {
        if (data?.gates == null || !Finite(ms)) return false;
        foreach (var gate in data.gates)
        {
            if (gate?.on == null) continue;
            foreach (var span in gate.on)
                if (span != null && ms >= span.time && ms < span.endTime) return true;
        }
        return false;
    }

    public enum GatePhase { None, Arriving, Pass, Mirror, Broken }

    // 門が奥から流れてくる時間(接近時間 − 0.5 秒)。門は止まる時刻のその秒数前から奥に見え、ノーツと同じ速さで来る。
    public static double ArriveSeconds(double approach) => Finite(approach) ? Math.Max(0, approach - CrossSeconds) : 0;

    // 門の今の状態(seconds は譜面の時計の秒)。止まる前(流れてくる)・素通し・鏡・割れた後(破片が飛ぶ間)。
    public static GatePhase GateState(ReverseGateData gate, double seconds, double approach)
    {
        if (gate == null || !Finite(seconds)) return GatePhase.None;
        double stop = gate.time / 1000.0, end = gate.endTime / 1000.0;
        if (seconds < stop - ArriveSeconds(approach)) return GatePhase.None;
        if (seconds < stop) return GatePhase.Arriving;
        if (seconds < end) return InSpan(gate, seconds * 1000.0) ? GatePhase.Mirror : GatePhase.Pass;
        if (seconds < end + ShatterSeconds) return GatePhase.Broken;
        return GatePhase.None;
    }

    static bool InSpan(ReverseGateData gate, double ms)
    {
        if (gate.on == null) return false;
        foreach (var span in gate.on) if (span != null && ms >= span.time && ms < span.endTime) return true;
        return false;
    }

    // 門の裏返りの角度(0 = 素通し・左青右赤、180 = 鏡・左赤右青)。鏡にする時刻から 0.33 秒で裏返り、素通しへ戻す時刻から 0.33 秒で戻る。
    // 割れる時刻に鏡の区間が終わるなら、鏡のまま割れる。
    public static float GateFlip(ReverseGateData gate, double seconds)
    {
        if (gate?.on == null || !Finite(seconds)) return 0f;
        float flip = 0f;
        foreach (var span in gate.on)
        {
            if (span == null) continue;
            double start = span.time / 1000.0, end = span.endTime / 1000.0;
            if (seconds < start) break;
            if (seconds < end) flip = 180f * Ease((seconds - start) / FlipSeconds);
            else if (span.endTime >= gate.endTime - .5f) flip = 180f;
            else flip = 180f * (1f - Ease((seconds - end) / FlipSeconds));
        }
        return flip;
    }

    // 床の判定線を左赤・右青に塗る量(0〜1)。立っている門の裏返りの量。
    public static float MirrorTint(ReverseChartData data, double seconds)
    {
        if (data?.gates == null || !Finite(seconds)) return 0f;
        float amount = 0f;
        foreach (var gate in data.gates)
        {
            if (gate == null || seconds < gate.time / 1000.0 || seconds >= gate.endTime / 1000.0) continue;
            amount = Mathf.Max(amount, GateFlip(gate, seconds) / 180f);
        }
        return amount;
    }

    // 予告音の時刻(ms): 鏡にする拍の3拍前・2拍前・1拍前。拍はテンポ表で秒に直す(拍 403 より後は約 200 BPM)。
    public static double[] CueTimesMs(ChartTempoMap tempo, double onMs)
    {
        var result = new double[CueCount];
        if (tempo == null || !Finite(onMs)) return result;
        double beat = tempo.BeatAt(onMs);
        for (int k = 0; k < CueCount; k++) result[k] = tempo.TimeAt(beat - (CueCount - k));
        return result;
    }

    // ---- 天地返し ----

    // 世界の向き(度)。turns の各区間で前の角度から degrees へ smoothstep で回し、区間の後はその角度のまま。きしみを足す。
    // F3(reduced)では回る動きを見せず、区間の真ん中で段にする(きしみも出さない)。
    public static float TurnAngle(ReverseChartData data, double seconds, bool reduced)
    {
        if (data == null || !Finite(seconds)) return 0f;
        float angle = 0f;
        if (data.turns != null)
            foreach (var turn in data.turns)
            {
                if (turn == null) continue;
                double start = turn.time / 1000.0, end = turn.endTime / 1000.0;
                if (seconds < start) break;
                if (seconds >= end) { angle = turn.degrees; continue; }
                double u = (seconds - start) / Math.Max(1e-6, end - start);
                angle = reduced ? (u < .5 ? angle : turn.degrees) : Mathf.Lerp(angle, turn.degrees, Ease(u));
                break;
            }
        if (!reduced) angle += CreakOffset(data.creaks, seconds);
        return angle;
    }

    // きしみ: 0.05 秒で ± degrees まで傾き、残りの 0.2 秒でなめらかに戻る。
    public static float CreakOffset(List<ReverseCreakData> creaks, double seconds)
    {
        if (creaks == null || !Finite(seconds)) return 0f;
        float sum = 0f;
        foreach (var creak in creaks)
        {
            if (creak == null) continue;
            double t = seconds - creak.time / 1000.0;
            if (t < 0 || t >= CreakSeconds) continue;
            float k = t < CreakRiseSeconds ? Ease(t / CreakRiseSeconds)
                : 1f - Ease((t - CreakRiseSeconds) / (CreakSeconds - CreakRiseSeconds));
            sum += creak.degrees * k;
        }
        return sum;
    }

    // F3 の暗転の濃さ(0〜0.7)。回転の区間の真ん中の前後 0.25 秒、出入りは 0.1 秒。
    public static float ReducedDim(ReverseChartData data, double seconds)
    {
        if (data?.turns == null || !Finite(seconds)) return 0f;
        float alpha = 0f;
        foreach (var turn in data.turns)
        {
            if (turn == null) continue;
            double middle = (turn.time + turn.endTime) / 2000.0;
            double distance = Math.Abs(seconds - middle);
            if (distance >= ReducedDimSeconds * .5) continue;
            alpha = Mathf.Max(alpha, ReducedDimAlpha * Mathf.Clamp01((float)((ReducedDimSeconds * .5 - distance) / ReducedDimRampSeconds)));
        }
        return alpha;
    }

    // 元の向きへ戻る一回転の着く時刻(ms)。回り終わりから 1 秒以内にテンポ表の点(曲の最後の拍)があればそこ、無ければ回り終わり。
    // 最後の大きな光と一打をここに置く(Manic Sonic は 151.267 秒に回り終わり、151.45 秒が曲の最後の拍)。
    public static double FinalHitMs(ReverseTurnData turn, ChartTempoMap tempo)
    {
        if (turn == null) return 0;
        double end = turn.endTime;
        if (tempo?.Points != null)
            foreach (var point in tempo.Points)
                if (point != null && point.timeMs >= end - .5 && point.timeMs <= end + 1000) return Math.Max(end, point.timeMs);
        return end;
    }

    // 角度を [0, 360) にそろえる(0 と 360 の近くは 0)。
    public static float Wrap(float degrees)
    {
        if (!Finite(degrees)) return 0f;
        float wrapped = degrees % 360f;
        if (wrapped < 0f) wrapped += 360f;
        return wrapped < 1e-4f || wrapped > 360f - 1e-4f ? 0f : wrapped;
    }

    // 画面が逆さ寄りか(90° より大きく 270° より小さい)。
    public static bool UpsideDown(float degrees)
    {
        float wrapped = Wrap(degrees);
        return wrapped > 90f && wrapped < 270f;
    }

    // ワールドの XY で軸(ノーツの帯の中心 G)のまわりに −θ 回す写し: B′ = G + R(−θ)(B − G)。180° なら (x, y) → (−x, −0.3 − y)。
    // 譜面の決まり(逆さの間のノーツは、画面で見えてほしい位置 S を W = (−S.x, −0.3 − S.y) に焼く)と、カメラが無いときの近似に使う。
    // 剣の入力は見下ろしの透視で少しずれる(本番のカメラで帯の端 0.05・帯の外 0.4)ので、正確な写し TryTurnInput を使う(WorldTurn.MapInput)。
    public static Vector2 InputRotate(Vector2 point, float degrees)
    {
        float wrapped = Wrap(degrees);
        if (wrapped == 0f) return point;
        Vector2 d = point - Pivot;
        if (Mathf.Abs(wrapped - 180f) < 1e-4f) return Pivot - d;
        float r = -wrapped * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
        return Pivot + new Vector2(c * d.x - s * d.y, s * d.x + c * d.y);
    }

    public static Vector3 InputRotate(Vector3 point, float degrees)
    {
        Vector2 p = InputRotate(new Vector2(point.x, point.y), degrees);
        return new Vector3(p.x, p.y, point.z);
    }

    // 天地返しの投影の行列 M × P。M は軸の点(NDC の pivot)のまわりに、縦横比をそろえた空間で θ 回す行列。
    // 左上の 2×2 は [[c, −s/a], [a·s, c]]、4列目は (I − 2×2)·pivot、z と w の行は単位行列。
    // 180° なら pivot についての点対称で、三角形の向きは変わらない(裏表の反転は要らない)。θ が 0 なら P のまま。
    public static Matrix4x4 ViewMatrix(Matrix4x4 projection, Vector2 pivotNdc, float degrees, float aspect)
    {
        float wrapped = Wrap(degrees);
        if (wrapped == 0f || !Finite(aspect) || aspect <= 0f) return projection;
        float c, s;
        if (Mathf.Abs(wrapped - 180f) < 1e-4f) { c = -1f; s = 0f; }
        else
        {
            float r = wrapped * Mathf.Deg2Rad;
            c = Mathf.Cos(r);
            s = Mathf.Sin(r);
        }
        var m = Matrix4x4.identity;
        m.m00 = c; m.m01 = -s / aspect;
        m.m10 = aspect * s; m.m11 = c;
        m.m03 = pivotNdc.x - (m.m00 * pivotNdc.x + m.m01 * pivotNdc.y);
        m.m13 = pivotNdc.y - (m.m10 * pivotNdc.x + m.m11 * pivotNdc.y);
        return m * projection;
    }

    // 剣の入力の正確な写し(2026-10-28 Codex の点検で近似から変えた)。点 P を回す前の投影 P0 で NDC へ写し、軸の NDC のまわりに
    // −θ 回し(縦横比をそろえた空間で)、P0 でその NDC から光線を出して、P と同じ z の平面と交わる点を P′ にする。
    // 絵は M(θ)·P0 で描くので、P′ は回した画面で P と同じ位置に見える(見下ろしの角度・カメラの高さ・帯の外でも正確)。
    // 0° は P のまま。写せないとき(点がカメラの後ろ・光線が平面と交わらない)は false。
    public static bool TryTurnInput(Vector3 point, float degrees, Matrix4x4 projection, Matrix4x4 worldToCamera,
        Vector2 pivotNdc, float aspect, out Vector3 turned)
    {
        turned = point;
        float wrapped = Wrap(degrees);
        if (wrapped == 0f) return true;
        if (!Finite(aspect) || aspect <= 0f) return false;
        Matrix4x4 viewProjection = projection * worldToCamera;
        Vector4 clip = viewProjection * new Vector4(point.x, point.y, point.z, 1f);
        if (!(clip.w > 1e-5f)) return false;
        float c, s;
        if (Mathf.Abs(wrapped - 180f) < 1e-4f) { c = -1f; s = 0f; }
        else
        {
            float r = -wrapped * Mathf.Deg2Rad;
            c = Mathf.Cos(r);
            s = Mathf.Sin(r);
        }
        float x = (clip.x / clip.w - pivotNdc.x) * aspect, y = clip.y / clip.w - pivotNdc.y;
        float ndcX = pivotNdc.x + (c * x - s * y) / aspect, ndcY = pivotNdc.y + (s * x + c * y);
        Matrix4x4 inverse = viewProjection.inverse;
        Vector4 near = inverse * new Vector4(ndcX, ndcY, -1f, 1f);
        Vector4 far = inverse * new Vector4(ndcX, ndcY, 1f, 1f);
        if (Mathf.Abs(near.w) < 1e-12f || Mathf.Abs(far.w) < 1e-12f) return false;
        Vector3 from = new Vector3(near.x, near.y, near.z) / near.w, to = new Vector3(far.x, far.y, far.z) / far.w;
        float dz = to.z - from.z;
        if (Mathf.Abs(dz) < 1e-8f) return false;
        float t = (point.z - from.z) / dz;
        if (!(t >= 0f) || float.IsInfinity(t)) return false;
        Vector3 hit = from + (to - from) * t;
        if (!Finite(hit.x) || !Finite(hit.y)) return false;
        turned = new Vector3(hit.x, hit.y, point.z);
        return true;
    }

    // ワールドの点が、投影 P とカメラの行列で写る NDC(−1〜1)。
    public static Vector2 PivotNdc(Matrix4x4 projection, Matrix4x4 worldToCamera, Vector3 world)
    {
        Vector4 clip = projection * (worldToCamera * new Vector4(world.x, world.y, world.z, 1f));
        if (Mathf.Abs(clip.w) < 1e-6f) return Vector2.zero;
        return new Vector2(clip.x / clip.w, clip.y / clip.w);
    }
}
