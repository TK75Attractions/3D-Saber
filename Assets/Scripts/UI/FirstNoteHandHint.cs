using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 譜面の並び順に依存せず、最初の同時ノーツが要求する手をまとめる。
internal static class FirstNoteHandHint
{
    internal static bool TryDescribe(ChartData chart, out string text, out Color color)
    {
        text = null;
        color = Color.white;
        if (chart?.notes == null) return false;
        double first = double.PositiveInfinity;
        foreach (var note in chart.notes)
            if (note != null && !float.IsNaN(note.time) && !float.IsInfinity(note.time))
                first = Math.Min(first, note.TimeSeconds);
        if (double.IsInfinity(first)) return false;
        bool red = false, blue = false, goldOnly = true, goldLeft = false, goldRight = false;
        foreach (var note in chart.notes)
        {
            if (note == null || !(Math.Abs(note.TimeSeconds - first) <= NoteSpawner.SimultaneousEpsilonSeconds)) continue;
            var hand = SaberHandHelper.FromColor(note.color);
            red |= hand == SaberHand.Right;
            blue |= hand == SaberHand.Left;
            bool gold = string.Equals(note.color, "gold", StringComparison.OrdinalIgnoreCase);
            goldOnly &= gold;
            goldLeft |= gold && note.x < 0;
            goldRight |= gold && note.x > 0;
        }
        // 金だけが左右に分かれて同時に来るときは両手が要る(2026-10-24 点検: escapade の最初の左右端の金2個に
        // 「どちらの手でも」と出ていた)。金と色つきの同時は、今までどおり色の手(近ければ1振りで切れる)。
        bool both = red && blue || !red && !blue && goldLeft && goldRight;
        text = both ? "両手から" : red ? "右手から" : blue ? "左手から" : "どちらの手でも";
        color = both ? Color.white : red ? UISkinPalette.LogoRed : blue ? UISkinPalette.LogoBlue
            : goldOnly ? UISkinPalette.NoteGold : Color.white;
        return true;
    }

    // endは到着の0.15秒前。フェードアウトもこの時刻までに終える。
    internal static float AlphaAt(double now, double begin, double end) =>
        Mathf.Min(Mathf.Clamp01((float)((now - begin) / .18)), Mathf.Clamp01((float)((end - now) / .18)));
}

// 最初の手の予告を、3・2・1 の部品とは別の画面に出す(3・2・1 の部品は START のすぐあとに消えるため)。
// 時刻は曲の時計(SongPlayer.SongTime、音源の頭からの秒)で持ち、ノーツと同じ時計で薄く出し入れして、
// 最初のノーツの0.15秒前までに消える。曲が止まっている間(剣の見失い・音の立て直し)は隠し、再開すれば戻る。
// 自分では Update を持たず、本編の GamePlayManager.Update が毎フレーム Tick を呼ぶ。シーンを出れば一緒に消える。
internal sealed class FirstNoteHandHintView : MonoBehaviour
{
    static FirstNoteHandHintView current;
    TextMeshProUGUI label;
    double begin, end;

    internal static double CurrentBegin => current != null ? current.begin : double.NaN;
    internal static double CurrentEnd => current != null ? current.end : double.NaN;

    internal static void Show(string text, Color color, double beginSongSeconds, double endSongSeconds)
    {
        Hide();
        var go = new GameObject("FirstHandHint", typeof(Canvas), typeof(CanvasScaler));
        var canvas = go.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 940;
        var scaler = go.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
        var view = go.AddComponent<FirstNoteHandHintView>();
        // 手の短い予告は従来の幅。ルールや段の字幕は横へ広げ、長い文だけ縮小を許す。
        bool longCaption = !string.IsNullOrEmpty(text) && text.Length > 14;
        view.label = UISkinKit.MakeTMP(go.transform, "Text", text, 44, color, TextAlignmentOptions.Center,
            new Vector2(0, -360), new Vector2(longCaption ? 1440 : 720, 64),
            FontStyles.Normal, 0, UISkinKit.JapaneseFallbackFontAsset());
        view.label.fontSizeMin = 28;
        view.label.fontSizeMax = 44;
        view.label.enableAutoSizing = longCaption;
        view.label.textWrappingMode = TextWrappingModes.NoWrap;
        view.label.raycastTarget = false;
        view.label.alpha = 0;
        view.begin = beginSongSeconds; view.end = endSongSeconds;
        current = view;
    }

    internal static void Hide()
    {
        if (current != null)
        {
            current.gameObject.SetActive(false);
            UISkinKit.SafeDestroy(current.gameObject);
        }
        current = null;
    }

    // 本編の Update から毎フレーム呼ぶ。曲の予約が無くなったら(止めて読み直す途中など)消す。
    internal static void Tick(SongPlayer song)
    {
        if (current == null) return;
        if (song == null || !song.IsScheduled) { Hide(); return; }
        Apply(song.SongTime, song.IsPaused);
    }

    internal static void Apply(double songSeconds, bool paused)
    {
        if (current == null) return;
        if (current.label != null) current.label.alpha = paused ? 0f : FirstNoteHandHint.AlphaAt(songSeconds, current.begin, current.end);
        if (!paused && songSeconds >= current.end) Hide();
    }

    void OnDestroy() { if (current == this) current = null; }
}
