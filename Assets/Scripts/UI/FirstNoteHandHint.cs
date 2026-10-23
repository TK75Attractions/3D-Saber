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
        bool red = false, blue = false, goldOnly = true;
        foreach (var note in chart.notes)
        {
            if (note == null || !(Math.Abs(note.TimeSeconds - first) <= NoteSpawner.SimultaneousEpsilonSeconds)) continue;
            var hand = SaberHandHelper.FromColor(note.color);
            red |= hand == SaberHand.Right;
            blue |= hand == SaberHand.Left;
            goldOnly &= string.Equals(note.color, "gold", StringComparison.OrdinalIgnoreCase);
        }
        text = red && blue ? "両手から" : red ? "右手から" : blue ? "左手から" : "どちらの手でも";
        color = red && blue ? Color.white : red ? UISkinPalette.LogoRed : blue ? UISkinPalette.LogoBlue
            : goldOnly ? UISkinPalette.NoteGold : Color.white;
        return true;
    }

    // endは到着の0.15秒前。フェードアウトもこの時刻までに終える。
    internal static float AlphaAt(double now, double begin, double end) =>
        Mathf.Min(Mathf.Clamp01((float)((now - begin) / .18)), Mathf.Clamp01((float)((end - now) / .18)));
}

// 最初の手の予告を、3・2・1 の部品とは別の画面に出す(3・2・1 の部品は START のすぐあとに消えるため)。
// 曲の DSP 時計で薄く出し入れし、最初のノーツの0.15秒前までに消える。シーンを出れば一緒に消える。
internal sealed class FirstNoteHandHintView : MonoBehaviour
{
    static FirstNoteHandHintView current;
    TextMeshProUGUI label;
    double begin, end;

    internal static void Show(string text, Color color, double beginDsp, double endDsp)
    {
        Hide();
        var go = new GameObject("FirstHandHint", typeof(Canvas), typeof(CanvasScaler));
        var canvas = go.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 940;
        var scaler = go.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
        var view = go.AddComponent<FirstNoteHandHintView>();
        view.label = UISkinKit.MakeTMP(go.transform, "Text", text, 44, color, TextAlignmentOptions.Center,
            new Vector2(0, -360), new Vector2(720, 64), FontStyles.Normal, 0, UISkinKit.JapaneseFallbackFontAsset());
        view.label.enableAutoSizing = false;
        view.label.textWrappingMode = TextWrappingModes.NoWrap;
        view.label.raycastTarget = false;
        view.label.alpha = 0;
        view.begin = beginDsp; view.end = endDsp;
        current = view;
    }

    internal static void Hide()
    {
        if (current != null) UISkinKit.SafeDestroy(current.gameObject);
        current = null;
    }

    void Update()
    {
        double now = AudioSettings.dspTime;
        if (label != null) label.alpha = FirstNoteHandHint.AlphaAt(now, begin, end);
        if (now >= end) Hide();
    }

    void OnDestroy() { if (current == this) current = null; }
}
