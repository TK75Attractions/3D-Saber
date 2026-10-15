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
    string folder, song, exported;
    readonly HashSet<string> drafts = new HashSet<string>();
    void Set(string field, object value) => typeof(SaberPlaybackReviewWindow).GetField(field, Flags).SetValue(window, value);
    T Get<T>(string field) => (T)typeof(SaberPlaybackReviewWindow).GetField(field, Flags).GetValue(window);
    void Invoke(string method) => typeof(SaberPlaybackReviewWindow).GetMethod(method, Flags).Invoke(window, null);
    void InvokeWith(string method, object value) => typeof(SaberPlaybackReviewWindow).GetMethod(method, Flags).Invoke(window, new[] { value });
    T Property<T>(string name) => (T)typeof(SaberPlaybackReviewWindow).GetProperty(name, Flags).GetValue(window);
    void Dirty(bool value) => typeof(UnityEditor.EditorWindow).GetProperty("hasUnsavedChanges").GetSetMethod(true).Invoke(window, new object[] { value });
    [SetUp] public void Setup()
    {
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
        if (window != null) { drafts.Add(Property<string>("DraftPath")); Dirty(false); window.Close(); }
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
    [UnityTest] public IEnumerator WindowDrawsAtMinimumSizeAndShortcutsDoNotInterruptTyping()
    {
        window.Show(); window.position = new Rect(40, 40, 1000, 660);
        Set("seconds", 2f); Set("rangeA", 1.5f); Set("rangeB", 3.5f); Set("instruction", "赤ノーツの配置を確認"); Invoke("AddComment");
        Set("timelineZoom", 2);
        for (int i = 0; i < 8; i++) { window.Repaint(); yield return null; }
        // 実際のIMGUIイベントで右から左へ区間を指定する。指を離した後にドラッグ状態を残さない。
        Rect timeline = Get<Rect>("timelineRect");
        Assert.Greater(timeline.width, 100); Assert.LessOrEqual(timeline.xMax, window.position.width - 340);
        Vector2 from = new Vector2(timeline.x + timeline.width * .8f, timeline.y + 28);
        Vector2 to = new Vector2(timeline.x + timeline.width * .2f, timeline.y + 28);
        window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = from, modifiers = EventModifiers.Shift });
        window.SendEvent(new Event { type = EventType.MouseDrag, button = 0, mousePosition = to, modifiers = EventModifiers.Shift });
        window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = to, modifiers = EventModifiers.Shift });
        Assert.AreEqual(1, Get<float>("rangeA"), .001f); Assert.AreEqual(4, Get<float>("rangeB"), .001f);
        Assert.IsFalse(Get<bool>("playing")); Assert.IsFalse(Get<bool>("timelineDragging"));
        Set("seconds", 2f);
        UnityEditor.EditorGUIUtility.editingTextField = false;
        window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.RightArrow });
        Assert.AreEqual(3, Get<float>("seconds"));
        window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.LeftArrow, modifiers = EventModifiers.Shift });
        Assert.AreEqual(3 - 1f / 60, Get<float>("seconds"), .0001f);
        UnityEditor.EditorGUIUtility.editingTextField = true;
        window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.RightArrow });
        Assert.AreEqual(3 - 1f / 60, Get<float>("seconds"), .0001f);
        UnityEditor.EditorGUIUtility.editingTextField = false;
        Set("instruction", "次の指摘");
        window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Return, modifiers = EventModifiers.Control });
        Assert.AreEqual(2, Get<List<SaberReviewComment>>("comments").Count);
        for (int i = 0; i < 5; i++) { window.Repaint(); yield return null; }
        Assert.That(Get<string>("status"), Does.Contain("指示を追加しました"));
    }
}
