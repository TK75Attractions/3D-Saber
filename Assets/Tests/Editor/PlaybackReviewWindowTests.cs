using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Saber.ChartEditor;
using UnityEngine;
using UnityEngine.TestTools;

public class PlaybackReviewWindowTests
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    SaberPlaybackReviewWindow window;
    AudioClip testClip;
    bool shown;
    string folder, song, exported;
    readonly HashSet<string> drafts = new HashSet<string>();
    void Set(string field, object value) => typeof(SaberPlaybackReviewWindow).GetField(field, Flags).SetValue(window, value);
    T Get<T>(string field) => (T)typeof(SaberPlaybackReviewWindow).GetField(field, Flags).GetValue(window);
    void Invoke(string method) => typeof(SaberPlaybackReviewWindow).GetMethod(method, Flags).Invoke(window, null);
    void InvokeWith(string method, object value) => typeof(SaberPlaybackReviewWindow).GetMethod(method, Flags).Invoke(window, new[] { value });
    object InvokeArgs(string method, params object[] args) => typeof(SaberPlaybackReviewWindow).GetMethod(method, Flags).Invoke(window, args);
    T Property<T>(string name) => (T)typeof(SaberPlaybackReviewWindow).GetProperty(name, Flags).GetValue(window);
    void Dirty(bool value) => typeof(UnityEditor.EditorWindow).GetProperty("hasUnsavedChanges").GetSetMethod(true).Invoke(window, new object[] { value });
    [SetUp] public void Setup()
    {
        shown = false;
        song = "__PlaybackReview_" + Guid.NewGuid().ToString("N");
        folder = Path.Combine(Application.streamingAssetsPath, "Songs", song); Directory.CreateDirectory(folder);
        var document = new SaberChartDocument();
        document.notes.Add(new SaberChartNote { time = 2500, color = "red", x = 1 });
        document.notes.Add(new SaberChartNote { time = 3000, color = "blue", x = -1 });
        File.WriteAllText(Path.Combine(folder, "chart_e.json"), SaberChartUtility.ToFileJson(document));
        window = ScriptableObject.CreateInstance<SaberPlaybackReviewWindow>(); Set("song", song); Set("difficulty", "e"); Invoke("LoadChart");
        drafts.Add(Property<string>("DraftPath"));
    }
    [TearDown] public void Cleanup()
    {
        if (window != null)
        {
            drafts.Add(Property<string>("DraftPath")); Dirty(false);
            if (shown) window.Close(); else UnityEngine.Object.DestroyImmediate(window);
        }
        if (testClip != null) UnityEngine.Object.DestroyImmediate(testClip);
        foreach (string path in drafts) if (File.Exists(path)) File.Delete(path);
        drafts.Clear();
        if (!string.IsNullOrEmpty(exported) && Directory.Exists(exported)) Directory.Delete(exported, true);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        if (File.Exists(folder + ".meta")) File.Delete(folder + ".meta");
    }
    [Test] public void ExportResumePreviewApplyAndSaveUseSameNotesAndDoNotTouchOtherDifficulty()
    {
        Set("seconds", 2f); Set("rangeA", 2f); Set("rangeB", 3.5f); Set("instruction", "赤ノーツを少し右へ");
        Set("hasPoint", true); Set("point", new Vector2(.6f, .45f)); Invoke("AddComment"); window.ExportRequest();
        exported = Get<string>("lastFolder");
        string requestFile = Path.Combine(exported, "request.json");
        var request = JsonUtility.FromJson<SaberReviewRequest>(File.ReadAllText(requestFile));
        Assert.AreEqual(1, request.comments.Count); Assert.IsTrue(File.Exists(Path.Combine(exported, "frame-" + request.comments[0].id + ".png")));
        Assert.Greater(new FileInfo(Directory.GetFiles(exported, "frame-*.png").Single()).Length, 10000, "実際の3D映像を保存する");
        window.ResumeRequestFile(requestFile);
        var replacement = request.chart.notes[0].Clone(); replacement.x = 1.7f;
        var patch = new SaberReviewPatch { songId = song, difficulty = "e", baseHash = request.baseHash, explanation = "赤を右へ", operations = new List<SaberReviewOperation> {
            new SaberReviewOperation { action = "replace", index = 0, note = replacement }
        } };
        string before = File.ReadAllText(Path.Combine(folder, "chart_e.json"));
        window.LoadReply(JsonUtility.ToJson(patch)); Assert.AreEqual(1, Get<SaberChartDocument>("document").notes[0].x);
        Assert.AreEqual(1.7f, Get<SaberChartDocument>("candidate").notes[0].x); Assert.AreEqual(before, File.ReadAllText(Path.Combine(folder, "chart_e.json")));
        UnityEngine.Object.DestroyImmediate(window);
        window = ScriptableObject.CreateInstance<SaberPlaybackReviewWindow>(); Set("song", song); Set("difficulty", "e"); Invoke("LoadChart");
        Assert.AreEqual(1.7f, Get<SaberChartDocument>("candidate").notes[0].x, "確認中の変更案も復元する");
        Assert.IsTrue(Property<bool>("ExportIsCurrent"));
        Invoke("ApplyCandidate"); Assert.IsTrue(window.hasUnsavedChanges);
        window.SaveChanges(); Assert.IsFalse(window.hasUnsavedChanges);
        Assert.AreEqual(1.7f, SaberChartUtility.FromJson(File.ReadAllText(Path.Combine(folder, "chart_e.json"))).notes[0].x);
        Assert.IsFalse(File.Exists(Path.Combine(folder, "chart_normal.json"))); Assert.IsFalse(File.Exists(Path.Combine(folder, "chart.json")));
    }
    [Test] public void ChangedDiskBlocksResumeAndSave()
    {
        Set("instruction", "時刻を調整"); Invoke("AddComment"); window.ExportRequest(); exported = Get<string>("lastFolder");
        string path = Path.Combine(folder, "chart_e.json"); string external = File.ReadAllText(path) + "\n "; File.WriteAllText(path, external);
        Assert.Throws<InvalidOperationException>(() => window.ResumeRequestFile(Path.Combine(exported, "request.json")));
        Dirty(true); window.SaveChanges(); Assert.IsTrue(window.hasUnsavedChanges); Assert.AreEqual(external, File.ReadAllText(path));
    }
    [Test] public void EditUpdatesSameCommentAndVisitRestoresExactSelection()
    {
        Set("seconds", 2.7f); Set("rangeA", 2f); Set("rangeB", 3.5f); Set("instruction", "赤を右へ");
        Get<HashSet<int>>("selected").Add(1); Invoke("AddComment");
        var original = Get<List<SaberReviewComment>>("comments").Single();
        Get<HashSet<int>>("selected").Clear(); InvokeWith("EditComment", original);
        CollectionAssert.AreEquivalent(new[] { 1 }, Get<HashSet<int>>("selected"));
        Assert.AreEqual(2.7f, Get<float>("seconds"));
        Set("instruction", "赤を少し左へ"); Invoke("AddComment");
        var updated = Get<List<SaberReviewComment>>("comments").Single();
        Assert.AreEqual(original.id, updated.id); Assert.AreEqual("赤を少し左へ", updated.instruction);
        InvokeWith("DeleteComment", updated); Assert.IsEmpty(Get<List<SaberReviewComment>>("comments"));
        Invoke("RestoreDeletedComment"); Assert.AreEqual(updated.id, Get<List<SaberReviewComment>>("comments").Single().id);
    }
    [Test] public void ClosingAndReopeningRestoresCommentDraftTextAndTargets()
    {
        Set("instruction", "保存した指摘"); Invoke("AddComment");
        Set("instruction", "まだ入力中の指摘"); Set("seconds", 2.6f); Set("rangeA", 2f); Set("rangeB", 4f);
        Get<HashSet<int>>("selected").Add(1); Set("hasPoint", true); Set("point", new Vector2(.2f, .8f));
        UnityEngine.Object.DestroyImmediate(window);
        window = ScriptableObject.CreateInstance<SaberPlaybackReviewWindow>(); Set("song", song); Set("difficulty", "e"); Invoke("LoadChart");
        Assert.AreEqual("まだ入力中の指摘", Get<string>("instruction"));
        Assert.AreEqual("保存した指摘", Get<List<SaberReviewComment>>("comments").Single().instruction);
        Assert.AreEqual(2.6f, Get<float>("seconds")); Assert.AreEqual(4, Get<float>("rangeB"));
        CollectionAssert.AreEquivalent(new[] { 1 }, Get<HashSet<int>>("selected"));
        Assert.AreEqual(new Vector2(.2f, .8f), Get<Vector2>("point"));
    }
    [Test] public void ChangedChartStartsNewDraftAndKeepsOldDraftFile()
    {
        Set("instruction", "以前の譜面への指摘"); Invoke("AddComment");
        string oldDraft = Property<string>("DraftPath");
        File.AppendAllText(Path.Combine(folder, "chart_e.json"), "\n "); Invoke("LoadChart");
        Assert.IsEmpty(Get<List<SaberReviewComment>>("comments")); Assert.AreEqual("", Get<string>("instruction"));
        Assert.AreNotEqual(oldDraft, Property<string>("DraftPath")); Assert.IsTrue(File.Exists(oldDraft));
    }
    [Test] public void UnconfirmedTextBlocksExportAndEditedCommentsInvalidateOldRequest()
    {
        Set("instruction", "最初の指摘"); Invoke("AddComment"); window.ExportRequest(); exported = Get<string>("lastFolder");
        Assert.IsTrue(Property<bool>("ExportIsCurrent"));
        Set("instruction", "入力中"); Assert.Throws<InvalidOperationException>(() => window.ExportRequest());
        Assert.IsFalse(Property<bool>("ExportIsCurrent")); Set("instruction", "");
        InvokeWith("EditComment", Get<List<SaberReviewComment>>("comments").Single());
        Set("instruction", "新しい指摘"); Invoke("AddComment");
        Assert.IsFalse(Property<bool>("ExportIsCurrent"));
        Assert.Throws<InvalidOperationException>(() => window.LoadReply("{}"));
    }
    [Test] public void EmptyCommentsAreNotAddedAndNavigationSetsUsefulRange()
    {
        Set("instruction", "  "); Invoke("AddComment"); Assert.IsEmpty(Get<List<SaberReviewComment>>("comments"));
        InvokeWith("JumpNote", 1); Assert.AreEqual(2.5f, Get<float>("seconds")); Assert.AreEqual(2.25f, Get<float>("rangeA"));
        InvokeWith("JumpNote", 1); Assert.AreEqual(3f, Get<float>("seconds"));
        InvokeWith("JumpNote", -1); Assert.AreEqual(2.5f, Get<float>("seconds"));
    }
    [Test] public void DiscardedChartChangesDoNotReappearFromDraft()
    {
        var chart = Get<SaberChartDocument>("document"); chart.notes[0].x = 2;
        Set("chartJson", SaberChartUtility.ToJson(chart, false)); Dirty(true); Invoke("SaveDraft");
        window.DiscardChanges(); Assert.IsFalse(window.hasUnsavedChanges);
        Invoke("LoadChart"); Assert.AreEqual(1, Get<SaberChartDocument>("document").notes[0].x);
        Assert.IsFalse(window.hasUnsavedChanges);
    }
    [Test] public void ContextReplayKeepsReportRangeAndStopsOrLoopsWithLeadIn()
    {
        Set("rangeA", 2f); Set("rangeB", 3.5f); Set("reviewLeadIn", 1f); Invoke("StartAudition");
        Assert.AreEqual(1, Get<float>("seconds")); Assert.IsTrue(Get<bool>("playing"));
        Set("seconds", 4f); Invoke("UpdatePlaybackBoundary");
        Assert.IsFalse(Get<bool>("playing")); Assert.AreEqual(3.85f, Get<float>("seconds"), .001f);
        Assert.AreEqual(2, Get<float>("rangeA")); Assert.AreEqual(3.5f, Get<float>("rangeB"));
        Set("loop", true); Invoke("StartAudition"); Set("seconds", 4f); Invoke("UpdatePlaybackBoundary");
        Assert.AreEqual(1, Get<float>("seconds")); Assert.IsTrue(Get<bool>("audition")); Assert.IsTrue(Get<bool>("playing"));
        Invoke("Stop"); Assert.IsFalse(Get<bool>("audition"));
        Set("rangeA", .2f); Invoke("StartAudition"); Assert.AreEqual(0, Get<float>("seconds"));
    }
    [Test] public void TimingCaptureGroupsArrivalsPreservesWrittenFeedbackAndExportsWithoutChangingChart()
    {
        string before = File.ReadAllText(Path.Combine(folder, "chart_e.json"));
        Set("timingCapture", true); Set("instruction", "別の指摘を入力中");
        InvokeWith("RecordTimingAt", 1.23f); InvokeWith("RecordTimingAt", 1.23f);
        Set("beatSnap", 1); InvokeWith("RecordTimingAt", 1.76f);
        CollectionAssert.AreEqual(new[] { 1.23f, 2f }, Get<List<float>>("pendingNoteSeconds"));
        Assert.Throws<InvalidOperationException>(() => window.ExportRequest());
        Invoke("FinishTimingCapture");
        Assert.AreEqual("別の指摘を入力中", Get<string>("instruction"));
        var comment = Get<List<SaberReviewComment>>("comments").Single();
        CollectionAssert.AreEqual(new[] { 1.23f, 2f }, comment.requestedNoteSeconds);
        Set("instruction", ""); InvokeWith("EditComment", comment); Set("instruction", "青赤交互に配置して");
        Set("rangeA", 1.5f); Set("rangeB", 1.7f); Invoke("AddComment");
        comment = Get<List<SaberReviewComment>>("comments").Single();
        Assert.AreEqual(1.23f, comment.fromSeconds); Assert.AreEqual(2f, comment.toSeconds);
        CollectionAssert.AreEqual(new[] { 1.23f, 2f }, comment.requestedNoteSeconds);
        window.ExportRequest(); exported = Get<string>("lastFolder");
        var request = JsonUtility.FromJson<SaberReviewRequest>(File.ReadAllText(Path.Combine(exported, "request.json")));
        CollectionAssert.AreEqual(comment.requestedNoteSeconds, request.comments.Single().requestedNoteSeconds);
        Assert.AreEqual(1, Directory.GetFiles(exported, "frame-*.png").Length, "連打しても画像は指摘単位にまとめる");
        Assert.AreEqual(before, File.ReadAllText(Path.Combine(folder, "chart_e.json"))); Assert.IsFalse(window.hasUnsavedChanges);
        Set("timingCapture", true); InvokeWith("RecordTimingAt", 3f);
        Assert.IsFalse(Property<bool>("ExportIsCurrent")); Assert.Throws<InvalidOperationException>(() => window.LoadReply("{}"));
    }
    [Test] public void UnfinishedTimingCaptureRestoresPausedAndCanBeFinishedOrUndone()
    {
        Set("timingCapture", true); InvokeWith("RecordTimingAt", 3f); InvokeWith("RecordTimingAt", 1f);
        UnityEngine.Object.DestroyImmediate(window); window = ScriptableObject.CreateInstance<SaberPlaybackReviewWindow>();
        Set("song", song); Set("difficulty", "e"); Invoke("LoadChart");
        Assert.IsFalse(Get<bool>("timingCapture")); Assert.IsFalse(Get<bool>("playing"));
        CollectionAssert.AreEqual(new[] { 3f, 1f }, Get<List<float>>("pendingNoteSeconds"));
        Invoke("UndoTiming"); CollectionAssert.AreEqual(new[] { 3f }, Get<List<float>>("pendingNoteSeconds"));
        Invoke("FinishTimingCapture");
        CollectionAssert.AreEqual(new[] { 3f }, Get<List<SaberReviewComment>>("comments").Single().requestedNoteSeconds);
        Assert.IsEmpty(Get<List<float>>("pendingNoteSeconds"));
    }
    [Test] public void TimingCaptureDoesNotUseStaleFrameTimeWhenAudioClockIsUnavailable()
    {
        testClip = AudioClip.Create("UnavailableReviewClock", 16000, 1, 16000, false);
        Set("clip", testClip); Set("playing", true); Set("timingCapture", true); Set("seconds", .5f);
        Invoke("CaptureTiming");
        Assert.IsEmpty(Get<List<float>>("pendingNoteSeconds"));
        Assert.That(Get<string>("status"), Does.Contain("再生位置を取得できません"));
    }
    [UnityTest] public IEnumerator TimingKeysAndClicksCaptureOnPressWithoutStoppingOrRepeating()
    {
        window.Show(); shown = true; window.position = new Rect(40, 40, 1000, 660); Set("timingCapture", true);
        for (int i = 0; i < 8; i++) { window.Repaint(); yield return null; }
        var taps = Get<List<float>>("pendingNoteSeconds");
        Vector2 origin = window.rootVisualElement.worldBound.position;
        UnityEditor.EditorGUIUtility.editingTextField = false;
        Set("playing", true); Set("seconds", 1.2f); Set("lastTick", UnityEditor.EditorApplication.timeSinceStartup);
        window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.T, modifiers = EventModifiers.None });
        Assert.AreEqual(1, taps.Count); Assert.AreEqual(1.2f, taps[0], .15f); Assert.IsTrue(Get<bool>("playing"));
        Set("seconds", 2f); Set("lastTick", UnityEditor.EditorApplication.timeSinceStartup);
        window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.T, modifiers = EventModifiers.None });
        Assert.AreEqual(1, taps.Count, "押しっぱなしのキーリピートを記録しない");
        window.SendEvent(new Event { type = EventType.KeyUp, keyCode = KeyCode.T, modifiers = EventModifiers.None });
        window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.T, modifiers = EventModifiers.None });
        Assert.AreEqual(2, taps.Count);
        window.SendEvent(new Event { type = EventType.KeyUp, keyCode = KeyCode.T, modifiers = EventModifiers.None });
        UnityEditor.EditorGUIUtility.editingTextField = true;
        window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.T, modifiers = EventModifiers.None });
        Assert.AreEqual(2, taps.Count, "文章入力中は記録しない");
        window.SendEvent(new Event { type = EventType.KeyUp, keyCode = KeyCode.T, modifiers = EventModifiers.None });
        UnityEditor.EditorGUIUtility.editingTextField = false;
        window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Backspace, modifiers = EventModifiers.None });
        window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Backspace, modifiers = EventModifiers.None });
        Assert.AreEqual(1, taps.Count);
        window.SendEvent(new Event { type = EventType.KeyUp, keyCode = KeyCode.Backspace, modifiers = EventModifiers.None });
        Set("seconds", 2.5f); Set("lastTick", UnityEditor.EditorApplication.timeSinceStartup);
        Vector2 framePoint = origin + Get<Rect>("reviewFrameRect").center;
        window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = framePoint, modifiers = EventModifiers.None });
        Assert.AreEqual(2, taps.Count); Assert.IsFalse(Get<bool>("hasPoint")); Assert.IsTrue(Get<bool>("playing"));
        window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = framePoint, modifiers = EventModifiers.None });
        Assert.AreEqual(2, taps.Count);
        Set("seconds", 3.2f); Set("lastTick", UnityEditor.EditorApplication.timeSinceStartup);
        Vector2 buttonPoint = origin + Get<Rect>("timingTapRect").center;
        window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = buttonPoint, modifiers = EventModifiers.None });
        Assert.AreEqual(3, taps.Count, "ボタンは離すまで待たずに押した瞬間を記録する");
        window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = buttonPoint, modifiers = EventModifiers.None });
        Assert.AreEqual(3, taps.Count);
        window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape, modifiers = EventModifiers.None });
        Assert.IsFalse(Get<bool>("playing")); Assert.IsFalse(Get<bool>("timingCapture"));
        Assert.AreEqual(3, Get<List<SaberReviewComment>>("comments").Single().requestedNoteSeconds.Length);
    }
    [Test] public void DirectNoteSelectionIncludesTargetsAndLongEndAndDoesNotMutateChart()
    {
        var chart = Get<SaberChartDocument>("document");
        chart.notes[1].type = "long"; chart.notes[1].count = 3; chart.notes[1].lengthMs = 1500;
        string before = SaberChartUtility.ToJson(chart);
        InvokeArgs("SelectTimelineNote", 1, false);
        CollectionAssert.AreEquivalent(new[] { 1 }, Get<HashSet<int>>("selected"));
        Assert.AreEqual(2.8f, Get<float>("seconds"), .001f); Assert.AreEqual(4.75f, Get<float>("rangeB"), .001f);
        InvokeArgs("SelectTimelineNote", 0, true);
        CollectionAssert.AreEquivalent(new[] { 0, 1 }, Get<HashSet<int>>("selected"));
        Assert.AreEqual(2.25f, Get<float>("rangeA"), .001f);
        InvokeArgs("SelectTimelineNote", 1, true);
        CollectionAssert.AreEquivalent(new[] { 0 }, Get<HashSet<int>>("selected"));
        Assert.AreEqual(before, SaberChartUtility.ToJson(chart));
    }
    [Test] public void AllReplyOperationsRemainNavigableAfterReopeningAndComparisonKeepsTime()
    {
        var chart = Get<SaberChartDocument>("document");
        var request = SaberPlaybackReviewData.Request(chart, song, "e", "chart_e.json", Array.Empty<SaberReviewComment>());
        Set("requestJson", JsonUtility.ToJson(request));
        var operations = Enumerable.Range(0, 14).Select(i => new SaberReviewOperation { action = "add", note = new SaberChartNote { time = 4000 + i * 500, color = "red" } }).ToList();
        window.LoadReply(JsonUtility.ToJson(new SaberReviewPatch { songId = song, difficulty = "e", baseHash = request.baseHash, operations = operations }));
        InvokeWith("VisitChange", 13); Assert.AreEqual(2, Get<int>("changePage"));
        Assert.AreEqual(10.25f, Get<float>("rangeA"), .001f); Assert.AreEqual(10.75f, Get<float>("rangeB"), .001f);
        float time = Get<float>("seconds"); Invoke("ToggleComparison"); Assert.IsFalse(Get<bool>("after"));
        Assert.AreEqual(time, Get<float>("seconds")); Assert.AreEqual(2, Get<SaberChartDocument>("document").notes.Count);
        Set("beatSnap", 3); Set("reviewLeadIn", 2f); Set("showWaveform", false);
        UnityEngine.Object.DestroyImmediate(window); window = ScriptableObject.CreateInstance<SaberPlaybackReviewWindow>();
        Set("song", song); Set("difficulty", "e"); Invoke("LoadChart");
        Assert.AreEqual(14, Property<SaberReviewPatch>("Reply").operations.Count);
        Assert.AreEqual(13, Get<int>("changeIndex")); Assert.AreEqual(3, Get<int>("beatSnap"));
        Assert.AreEqual(2, Get<float>("reviewLeadIn")); Assert.IsFalse(Get<bool>("showWaveform"));
    }
    [UnityTest] public IEnumerator WindowDrawsAtMinimumSizeAndShortcutsDoNotInterruptTyping()
    {
        window.Show(); shown = true; window.position = new Rect(40, 40, 1000, 660);
        testClip = AudioClip.Create("ReviewWaveformTest", 16000 * 12, 1, 16000, false);
        var samples = new float[16000 * 12]; for (int i = samples.Length / 2; i < samples.Length; i++) samples[i] = .25f;
        testClip.SetData(samples, 0); Set("clip", testClip);
        Set("seconds", 2f); Set("rangeA", 1.5f); Set("rangeB", 3.5f); Set("instruction", "赤ノーツの配置を確認"); Invoke("AddComment");
        Set("timelineZoom", 2);
        for (int i = 0; i < 8; i++) { window.Repaint(); yield return null; }
        var waveform = Get<object>("reviewWaveform");
        Assert.NotNull(waveform.GetType().GetProperty("Peaks").GetValue(waveform));
        Assert.AreEqual(0f, (float)waveform.GetType().GetMethod("Sample").Invoke(waveform, new object[] { .1f }), .001f);
        Assert.AreEqual(.5f, (float)waveform.GetType().GetMethod("Sample").Invoke(waveform, new object[] { .9f }), .001f);
        // 実際のIMGUIイベントで右から左へ区間を指定する。指を離した後にドラッグ状態を残さない。
        Rect timeline = Get<Rect>("timelineRect");
        Assert.Greater(timeline.width, 100); Assert.LessOrEqual(timeline.xMax, window.position.width - 340);
        // SendEventはホスト座標を受け取るため、タブ見出しを除いたIMGUI原点を加える。
        Vector2 origin = window.rootVisualElement.worldBound.position;
        Vector2 from = origin + new Vector2(timeline.x + timeline.width * .8f, timeline.y + 28);
        Vector2 to = origin + new Vector2(timeline.x + timeline.width * .2f, timeline.y + 28);
        window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = from, modifiers = EventModifiers.Shift });
        window.SendEvent(new Event { type = EventType.MouseDrag, button = 0, mousePosition = to, modifiers = EventModifiers.Shift });
        window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = to, modifiers = EventModifiers.Shift });
        Assert.AreEqual(1, Get<float>("rangeA"), .001f); Assert.AreEqual(4, Get<float>("rangeB"), .001f);
        Assert.IsFalse(Get<bool>("playing")); Assert.IsFalse(Get<bool>("timelineDragging"));
        // ノーツ本体のクリック、Ctrlでの追加選択、カーソルを中心にしたズームを確認する。
        var red = origin + new Vector2(timeline.x + timeline.width * .5f, timeline.y + 38);
        var blue = origin + new Vector2(timeline.x + timeline.width * .6f, timeline.y + 24);
        // 次の操作ではShiftを離した状態を明示する。
        window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = red, modifiers = EventModifiers.None });
        CollectionAssert.AreEquivalent(new[] { 0 }, Get<HashSet<int>>("selected"), "赤ノーツの本体を押すと選択する");
        Assert.AreEqual(2.3f, Get<float>("seconds"), .001f);
        window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = red, modifiers = EventModifiers.None });
        CollectionAssert.AreEquivalent(new[] { 0 }, Get<HashSet<int>>("selected"));
        window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = blue, modifiers = EventModifiers.Control });
        window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = blue, modifiers = EventModifiers.Control });
        CollectionAssert.AreEquivalent(new[] { 0, 1 }, Get<HashSet<int>>("selected"));
        window.SendEvent(new Event { type = EventType.ScrollWheel, mousePosition = blue, delta = new Vector2(0, -1), modifiers = EventModifiers.None });
        Assert.AreEqual(3, Get<int>("timelineZoom")); Assert.AreEqual(1.8f, Get<float>("timelineStart"), .001f);
        Set("beatSnap", 1); Set("timelineZoom", 2); Set("timelineStart", 0f);
        var snapPoint = origin + new Vector2(timeline.x + timeline.width * .223f, timeline.y + 5);
        window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = snapPoint, modifiers = EventModifiers.None });
        window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = snapPoint, modifiers = EventModifiers.None });
        Assert.AreEqual(1, Get<float>("seconds"), .001f);
        Set("seconds", 2f);
        UnityEditor.EditorGUIUtility.editingTextField = false;
        window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.RightArrow, modifiers = EventModifiers.None });
        Assert.AreEqual(3, Get<float>("seconds"));
        window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.LeftArrow, modifiers = EventModifiers.Shift });
        Assert.AreEqual(3 - 1f / 60, Get<float>("seconds"), .0001f);
        UnityEditor.EditorGUIUtility.editingTextField = true;
        window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.RightArrow, modifiers = EventModifiers.None });
        Assert.AreEqual(3 - 1f / 60, Get<float>("seconds"), .0001f);
        UnityEditor.EditorGUIUtility.editingTextField = false;
        Set("instruction", "次の指摘");
        window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Return, modifiers = EventModifiers.Control });
        Assert.AreEqual(2, Get<List<SaberReviewComment>>("comments").Count);
        for (int i = 0; i < 5; i++) { window.Repaint(); yield return null; }
        Assert.That(Get<string>("status"), Does.Contain("指示を追加しました"));
        var request = SaberPlaybackReviewData.Request(Get<SaberChartDocument>("document"), song, "e", "chart_e.json", Get<List<SaberReviewComment>>("comments"));
        Set("requestJson", JsonUtility.ToJson(request));
        var patch = new SaberReviewPatch { songId = song, difficulty = "e", baseHash = request.baseHash,
            operations = Enumerable.Range(0, 9).Select(i => new SaberReviewOperation { action = "add", note = new SaberChartNote { time = 4000 + i * 100 } }).ToList() };
        window.LoadReply(JsonUtility.ToJson(patch)); InvokeWith("VisitChange", 8);
        for (int i = 0; i < 5; i++) { window.Repaint(); yield return null; }
        Assert.That(Get<string>("status"), Does.Contain("変更案 9件"));
        UnityEditor.EditorGUIUtility.editingTextField = false;
        float compareTime = Get<float>("seconds");
        window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.B, modifiers = EventModifiers.None });
        Assert.IsFalse(Get<bool>("after")); Assert.AreEqual(compareTime, Get<float>("seconds"));
    }
}
