using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Saber.ChartEditor;
using UnityEngine;

public class PlaybackReviewWindowTests
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    SaberPlaybackReviewWindow window;
    string folder, song, exported;
    void Set(string field, object value) => typeof(SaberPlaybackReviewWindow).GetField(field, Flags).SetValue(window, value);
    T Get<T>(string field) => (T)typeof(SaberPlaybackReviewWindow).GetField(field, Flags).GetValue(window);
    void Invoke(string method) => typeof(SaberPlaybackReviewWindow).GetMethod(method, Flags).Invoke(window, null);
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
    }
    [TearDown] public void Cleanup()
    {
        if (window != null) { Dirty(false); UnityEngine.Object.DestroyImmediate(window); }
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
}
