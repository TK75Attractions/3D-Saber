using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Saber.ChartEditor;
using UnityEditor;
using UnityEngine;

// 専用の架空曲で保存途中の失敗を再現し、利用者の譜面は変更しない。
public class ChartSaveTransactionTests
{
    static readonly Type Store = typeof(SaberChartEditorWindow).Assembly.GetType("Saber.ChartEditor.SaberChartFileStore");
    string songId, folder, normal, legacy;
    const string OldNormal = "{\"bpm\":111,\"offsetMs\":12,\"notes\":[]}";
    const string OldLegacy = "{\"bpm\":112,\"offsetMs\":13,\"notes\":[]}";

    [SetUp]
    public void SetUp()
    {
        songId = "__ChartSaveTest_" + Guid.NewGuid().ToString("N");
        folder = Path.Combine(Application.streamingAssetsPath, "Songs", songId);
        normal = Path.Combine(folder, "chart_normal.json");
        legacy = Path.Combine(folder, "chart.json");
        Directory.CreateDirectory(folder);
        File.WriteAllText(normal, OldNormal);
        File.WriteAllText(legacy, OldLegacy);
    }

    [TearDown]
    public void TearDown()
    {
        string root = Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, "Songs")) + Path.DirectorySeparatorChar;
        Assert.IsTrue(Path.GetFullPath(folder).StartsWith(root, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(Path.GetFileName(folder).StartsWith("__ChartSaveTest_"));
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        if (File.Exists(folder + ".meta")) File.Delete(folder + ".meta");
        string backups = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "3DSaberChartBackups"));
        if (Directory.Exists(backups))
            foreach (string file in Directory.GetFiles(backups, songId + "_*.json")) File.Delete(file);
        AssetDatabase.Refresh();
    }

    [Test]
    public void LockedLegacyRestoresOriginalOrRetainsRecoveryWhenRestoreIsLocked()
    {
        AssetDatabase.Refresh();
        string normalAsset = "Assets/StreamingAssets/Songs/" + songId + "/chart_normal.json";
        string originalGuid = AssetDatabase.AssetPathToGUID(normalAsset);
        Assert.IsNotEmpty(originalGuid);
        // 読み取りは許可し、バックアップ後の書き込みだけを失敗させる。
        IOException failure;
        using (new FileStream(legacy, FileMode.Open, FileAccess.Read, FileShare.Read))
            failure = Assert.Throws<IOException>(() => Save("normal"));
        if (File.ReadAllText(normal) != OldNormal)
        {
            // OSやインポーターが復元も阻む場合は、明示エラーと元データの退避が契約。
            // 復元できたことにせず、復旧可能な内容と場所が残っていることを検証する。
            Assert.IsInstanceOf<AggregateException>(failure.InnerException);
            Assert.GreaterOrEqual(((AggregateException)failure.InnerException).InnerExceptions.Count, 2);
            var recovery = Directory.GetDirectories(folder, ".chart-save-*");
            Assert.AreEqual(1, recovery.Length);
            StringAssert.Contains(Path.GetFullPath(recovery[0]), failure.Message);
            string previous = Path.Combine(recovery[0], "0.previous");
            Assert.AreEqual(OldNormal, File.ReadAllText(previous));
            Assert.AreEqual(OldLegacy, File.ReadAllText(legacy));
            // 架空曲だけを退避内容から戻し、同じ保存の再試行まで確認する。
            File.Copy(previous, normal, true);
            Directory.Delete(recovery[0], true);
        }
        Assert.AreEqual(OldNormal, File.ReadAllText(normal));
        Assert.AreEqual(OldLegacy, File.ReadAllText(legacy));
        AssertNoTemporaryDirectory();
        Assert.AreEqual(originalGuid, AssetDatabase.AssetPathToGUID(normalAsset));
        // ロック解除後は、同じ編集内容をそのまま保存し直せる。
        Save("normal");
        Assert.AreEqual(File.ReadAllText(normal), File.ReadAllText(legacy));
        Assert.AreEqual(333f, ChartLoader.LoadFromFile(normal).notes[0].time);
        Assert.AreEqual(originalGuid, AssetDatabase.AssetPathToGUID(normalAsset));
        AssertNoTemporaryDirectory();
    }

    [Test]
    public void FailedFirstNormalSaveDoesNotLeaveANewDifficultyFile()
    {
        File.Delete(normal);
        using (new FileStream(legacy, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => Save("normal"));
        Assert.False(File.Exists(normal), "失敗した保存の新規譜面を残さない");
        Assert.AreEqual(OldLegacy, File.ReadAllText(legacy));
        AssertNoTemporaryDirectory();
    }

    [Test]
    public void BlockedFirstFallbackDoesNotLeaveAnOrphanEasyChart()
    {
        File.Delete(legacy);
        Directory.CreateDirectory(legacy);
        Assert.Catch<Exception>(() => Save("easy"));
        Assert.False(File.Exists(Path.Combine(folder, "chart_easy.json")));
        Assert.True(Directory.Exists(legacy), "既存の保存先障害物は勝手に削除しない");
        Assert.AreEqual(OldNormal, File.ReadAllText(normal));
        AssertNoTemporaryDirectory();
    }

    [Test]
    public void LockedSpecificChartLeavesTheFallbackUntouched()
    {
        using (new FileStream(normal, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => Save("normal"));
        Assert.AreEqual(OldNormal, File.ReadAllText(normal));
        Assert.AreEqual(OldLegacy, File.ReadAllText(legacy));
        AssertNoTemporaryDirectory();
    }

    [Test]
    public void SuccessfulNormalSaveSynchronizesBothChartsAndKeepsAssetGuids()
    {
        AssetDatabase.Refresh();
        string normalAsset = "Assets/StreamingAssets/Songs/" + songId + "/chart_normal.json";
        string baseAsset = "Assets/StreamingAssets/Songs/" + songId + "/chart.json";
        string normalGuid = AssetDatabase.AssetPathToGUID(normalAsset);
        string baseGuid = AssetDatabase.AssetPathToGUID(baseAsset);
        Assert.IsNotEmpty(normalGuid);
        Assert.IsNotEmpty(baseGuid);
        Assert.AreEqual(Path.GetFullPath(normal), Path.GetFullPath(Save("Normal")));
        Assert.AreEqual(File.ReadAllText(normal), File.ReadAllText(legacy));
        var loaded = ChartLoader.LoadFromFile(normal);
        Assert.AreEqual(333f, loaded.notes[0].time);
        Assert.AreEqual(22f, loaded.offsetMs);
        Assert.AreEqual(normalGuid, AssetDatabase.AssetPathToGUID(normalAsset));
        Assert.AreEqual(baseGuid, AssetDatabase.AssetPathToGUID(baseAsset));
        AssertNoTemporaryDirectory();
    }

    [TestCase("easy")]
    [TestCase("hard")]
    public void SavingAnotherDifficultyDoesNotOverwriteExistingFallback(string difficulty)
    {
        string destination = Save(difficulty);
        Assert.True(File.Exists(destination));
        Assert.AreEqual(OldNormal, File.ReadAllText(normal));
        Assert.AreEqual(OldLegacy, File.ReadAllText(legacy));
        AssertNoTemporaryDirectory();
    }

    [TestCase("easy")]
    [TestCase("normal")]
    [TestCase("hard")]
    public void FirstSaveCreatesTheRequiredFallback(string difficulty)
    {
        File.Delete(legacy);
        string destination = Save(difficulty);
        Assert.AreEqual(File.ReadAllText(destination), File.ReadAllText(legacy));
        AssertNoTemporaryDirectory();
    }

    string Save(string difficulty)
    {
        var document = new SaberChartDocument { bpm = 123f, offsetMs = 22f };
        document.notes.Add(new SaberChartNote { beat = 0.68265f, time = 333f, x = .5f, y = .2f });
        try { return (string)Store.GetMethod("Save").Invoke(null, new object[] { document, songId, difficulty }); }
        catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
    }

    void AssertNoTemporaryDirectory()
    {
        Assert.IsEmpty(Directory.GetDirectories(folder, ".chart-save-*"));
    }
}
