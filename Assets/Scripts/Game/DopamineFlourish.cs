using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 最後の組と金の印を曲時計で駆動する。採点・ノーツの位置には書き込まない。
public sealed class DopamineFlourish : MonoBehaviour
{
    static DopamineFlourish current;
    GamePlayManager owner;
    NoteSpawner spawner;
    SongPlayer song;
    GoldNoteSfx gold;
    ChartTempoMap tempo;
    FinalNoteGroup finalGroup;
    StageFeverShow fever;
    JudgeGateFrame gate;
    Camera view;
    PlayExcitementMesh marks, frames, stageLight;
    Canvas stampCanvas;
    TextMeshProUGUI stamp;
    AudioSource source;
    AudioClip chord;
    readonly HashSet<CuttableNote> tracked = new HashSet<CuttableNote>();
    readonly Dictionary<CuttableNote, uint> successful = new Dictionary<CuttableNote, uint>();
    readonly List<(SlicePieceDecay piece, double time)> pieces = new List<(SlicePieceDecay, double)>();
    readonly float[] markAlpha = new float[3];
    double lastClock = double.NaN, finishAt = double.NaN, musicEnd;
    int resolved;
    bool failed, enabledFinale, stopped;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { current = null; }

    public static void Create(GamePlayManager owner, ChartData chart, StagePerformanceTimeline timeline, double musicEnd)
    {
        if (owner.GetComponent<DopamineFlourish>() != null) return;
        var show = owner.gameObject.AddComponent<DopamineFlourish>();
        current = show;
        show.owner = owner;
        show.spawner = owner.noteSpawner;
        show.song = owner.songPlayer;
        show.gold = owner.goldNoteSfx;
        show.musicEnd = musicEnd;
        show.tempo = new ChartTempoMap(chart.bpm, chart.beatZeroMs, chart.editorTempoMap);
        show.finalGroup = new FinalNoteGroup(chart, show.spawner.TotalOffsetSeconds, show.spawner.secondsPerLongCut);
        show.enabledFinale = show.finalGroup.Indices.Count > 0 && !(chart.aim?.HasContent ?? false)
            && !(chart.bull?.HasContent ?? false);
        var gateObject = GameObject.Find("JudgeGate");
        show.gate = gateObject != null ? gateObject.GetComponent<JudgeGateFrame>() : null;
        show.view = Camera.main;
        show.fever = UnityEngine.Object.FindFirstObjectByType<StageFeverShow>();
        show.Build(VoltageMusicKey.Parse(timeline != null ? timeline.key : null));
        show.spawner.OnNoteSpawned += show.Track;
        show.spawner.OnChartReset += show.StopPresentation;
        foreach (var note in show.spawner.LiveNotes) show.Track(note);
    }

    void Build(VoltageMusicKey key)
    {
        if (gate != null)
            marks = new PlayExcitementMesh(gate.transform, "GoldStairs", 3011, 0);
        frames = new PlayExcitementMesh(transform, "FinalNoteFrames", 3009, -1);
        if (fever != null)
            stageLight = new PlayExcitementMesh(fever.transform, "FinalStageLight", 3000, -190);

        var go = new GameObject("ClearStamp", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        go.transform.SetParent(transform, false);
        stampCanvas = go.GetComponent<Canvas>();
        stampCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        stampCanvas.sortingOrder = 504;
        var scaler = go.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = .5f;
        stamp = UISkinKit.MakeTMP(go.transform, "Stamp", "CLEAR", 56, UISkinPalette.NoteGold,
            TextAlignmentOptions.Center, Vector2.zero, new Vector2(260, 80),
            FontStyles.Bold, 0, UISkinKit.FontAsset("Oxanium-Bold"));
        stamp.raycastTarget = false;
        stampCanvas.enabled = false;

        if (Application.isPlaying)
        {
            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0;
            string name = FlourishSfx.FinaleName(key);
            chord = ProceduralSfx.Clip(name, FlourishSfx.Cached(name, () => FlourishSfx.FinaleChord(key)));
        }
    }

    // M はノイズの上へ新しい札・枠・くじを出さない。既存の情報の帯には触れない。
    public static bool Veiled(NoteSpawner spawner) => spawner != null && spawner.MemoryVeilMode;

    bool Advancing => !stopped && owner != null && owner.isActiveAndEnabled && song != null &&
        song.IsPlaying && !song.IsPaused && !song.IsWaitingForAudio && Time.timeScale > 0;

    public static float SongDelta(SongPlayer song, ref double previous)
    {
        if (song == null) return 0;
        double now = song.SongTime, elapsed = now - previous;
        previous = now;
        if (!song.IsPlaying || song.IsPaused || song.IsWaitingForAudio || Time.timeScale <= 0 ||
            double.IsNaN(elapsed) || double.IsInfinity(elapsed) || elapsed < 0 || elapsed > .5)
            return 0;
        return (float)elapsed;
    }

    public static float CutDelta(NoteSpawner spawner, float fallback, ref double previous)
        => current != null && current.spawner == spawner
            ? SongDelta(current.song, ref previous) : fallback;

    public static void TickFor(GamePlayManager owner)
    {
        var show = owner.GetComponent<DopamineFlourish>();
        if (show != null && show.Advancing) show.Tick();
    }

    void Track(CuttableNote note)
    {
        if (note == null) return;
        successful.Remove(note);
        if (!enabledFinale || note.IsFinalized ||
            !finalGroup.Contains(note.HitTime + spawner.LingerSecondsFor(note)) || !tracked.Add(note))
            return;
        note.OnJudged += Judged;
        note.OnMiss += Missed;
        note.OnRetired += Retired;
    }

    void Untrack(CuttableNote note)
    {
        if (ReferenceEquals(note, null)) return;
        note.OnJudged -= Judged;
        note.OnMiss -= Missed;
        note.OnRetired -= Retired;
        tracked.Remove(note);
    }

    void Retired(CuttableNote note)
    {
        if (tracked.Contains(note)) Resolve(note, false);
    }

    void Missed(CuttableNote note) => Resolve(note, false);

    void Judged(CuttableNote note, JudgmentTier tier, Vector3 point, Vector3 velocity)
        => Resolve(note, note.IsCut && !note.IsMissed);

    void Resolve(CuttableNote note, bool cut)
    {
        if (!tracked.Contains(note)) return;
        Untrack(note);
        if (!Advancing) { failed = true; return; }
        resolved++;
        failed |= !cut;
        if (cut) successful[note] = note.SpawnVersion;
        if (resolved != finalGroup.Indices.Count || failed) return;
        finishAt = song.SongTime;
        if (Veiled(spawner)) return;
        if (source != null && chord != null)
            source.PlayOneShot(ProceduralSfx.Revive(ref chord), 1);
        if (!DisplaySettings.ReducedEffects)
        {
            PlayExcitement.Current?.TryLargeFlash(
                LargeFlashKind.Full, LargeFlashTint.White, .7f, .15f);
            PlayExcitement.Current?.PushBackground(.3f, .08f);
        }
    }

    public static bool HoldPiece(CuttableNote note, SlicePieceDecay piece)
    {
        var show = current;
        if (show == null || piece == null || !show.Advancing || DisplaySettings.ReducedEffects ||
            Veiled(show.spawner) || !show.successful.TryGetValue(note, out uint version) ||
            version != note.SpawnVersion) return false;
        piece.Hold(.4f, .1f);
        piece.FlourishDriven = true;
        show.pieces.Add((piece, show.song.SongTime));
        return true;
    }

    void Tick()
    {
        float dt = SongDelta(song, ref lastClock);
        double now = song.SongTime;
        gold?.TickStaircase(now);
        for (int i = pieces.Count - 1; i >= 0; i--)
        {
            var item = pieces[i];
            if (item.piece == null || !item.piece.FlourishDriven ||
                !item.piece.gameObject.activeInHierarchy)
            {
                pieces.RemoveAt(i);
                continue;
            }
            double elapsed = now - item.time;
            if (elapsed >= 0 && elapsed <= .5) item.piece.Step((float)elapsed);
            pieces[i] = (item.piece, now);
        }
        DrawMarks(dt);
        DrawFrames(now);
        DrawEnding(now);
    }

    void DrawMarks(float dt)
    {
        if (marks == null) return;
        marks.Clear();
        for (int i = 0; i < 3; i++)
        {
            markAlpha[i] = gold != null && i < gold.StairCount
                ? 1 : Mathf.Max(0, markAlpha[i] - dt / .25f);
            if (markAlpha[i] == 0) continue;
            Vector3 center = new Vector3((i - 1) * .32f, gate.HalfHeight + .62f, -.12f);
            Color tint = UISkinPalette.NoteGold;
            tint.a = markAlpha[i];
            for (int side = 0; side < 4; side++)
            {
                float a = side * Mathf.PI * .5f, b = (side + 1) * Mathf.PI * .5f;
                marks.Band(center + new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0) * .11f,
                    center + new Vector3(Mathf.Cos(b), Mathf.Sin(b), 0) * .11f, .045f, tint);
            }
        }
        marks.Flush();
    }

    void DrawFrames(double now)
    {
        frames.Clear();
        double beat = tempo.BeatAt((now - spawner.TotalOffsetSeconds) * 1000);
        double lastBeat = tempo.BeatAt((finalGroup.LastTime - spawner.TotalOffsetSeconds) * 1000);
        if (!Veiled(spawner) && beat >= lastBeat - 2)
            foreach (var note in tracked)
            {
                if (note == null || note.IsFinalized || note.HidesCues ||
                    note.BodyRenderer == null || !note.BodyRenderer.enabled) continue;
                Bounds bounds = note.BodyRenderer.bounds;
                for (int ring = 0; ring < 2; ring++)
                {
                    float pad = .08f + ring * .10f, z = bounds.min.z - .02f;
                    Vector3 a = transform.InverseTransformPoint(
                        new Vector3(bounds.min.x - pad, bounds.min.y - pad, z));
                    Vector3 b = transform.InverseTransformPoint(
                        new Vector3(bounds.max.x + pad, bounds.min.y - pad, z));
                    Vector3 c = transform.InverseTransformPoint(
                        new Vector3(bounds.max.x + pad, bounds.max.y + pad, z));
                    Vector3 d = transform.InverseTransformPoint(
                        new Vector3(bounds.min.x - pad, bounds.max.y + pad, z));
                    frames.Band(a, b, .025f, UISkinPalette.NoteGold);
                    frames.Band(b, c, .025f, UISkinPalette.NoteGold);
                    frames.Band(c, d, .025f, UISkinPalette.NoteGold);
                    frames.Band(d, a, .025f, UISkinPalette.NoteGold);
                }
            }
        frames.Flush();
    }

    bool GateRect(out FlashRect rect)
    {
        rect = default;
        if (view == null || gate == null) return false;
        double left = 1, right = 0, bottom = 1, top = 0;
        for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            {
                Vector3 p = view.WorldToViewportPoint(gate.transform.TransformPoint(
                    new Vector3(x * gate.HalfWidth, y * gate.HalfHeight, 0)));
                if (p.z <= view.nearClipPlane) return false;
                left = Math.Min(left, p.x);
                right = Math.Max(right, p.x);
                bottom = Math.Min(bottom, p.y);
                top = Math.Max(top, p.y);
            }
        rect = new FlashRect(left, bottom, right, top);
        return true;
    }

    void DrawEnding(double now)
    {
        double age = now - finishAt;
        bool hit = !double.IsNaN(finishAt);
        FlashRect rect = default;
        bool visible = !Veiled(spawner) && GateRect(out rect);
        stageLight?.Clear();
        if (visible && hit && age >= 0 && age < .3 &&
            !DisplaySettings.ReducedEffects && stageLight != null)
        {
            stageLight.Protect(LargeFlashLayout.ProtectedGate(rect));
            fever.DrawFinale(stageLight, now, 1 - (float)(age / .3));
        }
        stageLight?.Flush();

        bool show = visible && (hit ? age >= .3 : now >= musicEnd && musicEnd > 0);
        stampCanvas.enabled = show;
        if (!show) return;
        bool small = !hit;
        float scale = small ? .65f : DisplaySettings.ReducedEffects ? 1
            : 1 + .18f * Mathf.Clamp01(1 - (float)(age - .3) / .18f);
        stamp.rectTransform.localScale = Vector3.one * scale;
        float unit = stampCanvas.scaleFactor;
        float half = 40 * 1.18f * unit;
        float y = Mathf.Max(Screen.height * .78f,
            view.pixelRect.y + (float)rect.Top * view.pixelHeight + 24 * unit + half);
        if (y + half > Screen.height - 12 * unit)
        {
            stampCanvas.enabled = false;
            return;
        }
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
            (RectTransform)stampCanvas.transform, new Vector2(Screen.width * .5f, y),
            null, out var local))
            stamp.rectTransform.anchoredPosition = local;
    }

    public static void SyncPause(NoteSpawner spawner, bool paused)
    {
        if (current == null || current.spawner != spawner || current.source == null) return;
        if (paused) current.source.Pause(); else current.source.UnPause();
    }

    public static void StopFor(NoteSpawner spawner)
    {
        if (current != null && current.spawner == spawner) current.StopPresentation();
    }

    void StopPresentation()
    {
        if (stopped) return;
        stopped = true;
        foreach (var note in new List<CuttableNote>(tracked)) Untrack(note);
        foreach (var item in pieces)
            if (item.piece != null) item.piece.FlourishDriven = false;
        pieces.Clear();
        successful.Clear();
        marks?.Hide();
        frames?.Hide();
        stageLight?.Hide();
        if (stampCanvas != null) stampCanvas.enabled = false;
        if (source != null) source.Stop();
    }

    void OnDisable() { StopPresentation(); }

    void OnDestroy()
    {
        StopPresentation();
        if (spawner != null)
        {
            spawner.OnNoteSpawned -= Track;
            spawner.OnChartReset -= StopPresentation;
        }
        marks?.Dispose();
        frames?.Dispose();
        stageLight?.Dispose();
        UISkinKit.SafeDestroy(chord);
        UISkinKit.SafeDestroy(source);
        if (stampCanvas != null) UISkinKit.SafeDestroy(stampCanvas.gameObject);
        if (current == this) current = null;
    }
}
