using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// 合成音の所有者だけが生成・解放し、共有音源を破棄しないことを確認する。
public class SfxLifetimePlayTests
{
    HashSet<int> initialClips;
    readonly List<GameObject> objects = new List<GameObject>();
    AudioClip borrowed;
    Scene initialScene;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        initialClips = new HashSet<int>(Resources.FindObjectsOfTypeAll<AudioClip>().Select(c => c.GetInstanceID()));
        initialScene = SceneManager.GetActiveScene();
        yield return null;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        foreach (var go in objects) if (go != null) Object.Destroy(go);
        objects.Clear();
        if (borrowed != null) Object.Destroy(borrowed);
        var scene = SceneManager.GetActiveScene();
        if (scene != initialScene)
        {
            SceneManager.SetActiveScene(SceneManager.CreateScene("SfxLifetimeCleanup"));
            yield return SceneManager.UnloadSceneAsync(scene);
        }
        yield return null;
        // 修正前の再現試験でも、今回生成した合成音だけを後片付けする。
        foreach (var clip in NewGeneratedClips()) Object.Destroy(clip);
        yield return null;
    }

    [UnityTest]
    public IEnumerator RepeatedDiscSelectionReusesTargetsAndReleasesTheScene()
    {
        yield return SceneManager.LoadSceneAsync("SongSelect");
        SongSelectController controller = null;
        SongSelectSkin skin = null;
        double deadline = Time.realtimeSinceStartupAsDouble + 30;
        while (Time.realtimeSinceStartupAsDouble < deadline)
        {
            controller = Object.FindFirstObjectByType<SongSelectController>();
            skin = Object.FindFirstObjectByType<SongSelectSkin>();
            if (controller != null && skin != null && skin.IsReady) break;
            yield return null;
        }
        Assert.NotNull(controller);
        Assert.NotNull(skin);
        Assert.IsTrue(skin.IsReady, "ディスク選曲画面の構築が完了していること");
        foreach (var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None)) judge.enabled = false;
        var aim = Object.FindFirstObjectByType<SongSelectAimPointer>();
        Assert.NotNull(aim);
        aim.enabled = false;
        int songCount = controller.SongCount;
        Assert.Greater(songCount, 1);
        var allTargets = Object.FindObjectsByType<SongSelectDiscTarget>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(target => target.gameObject.scene == SceneManager.GetActiveScene());
        var targets = Enumerable.Range(0, songCount)
            .Select(index => allTargets.SingleOrDefault(target => target.gameObject.name == "SongDisc_" + index))
            .ToArray();
        Assert.AreEqual(songCount, targets.Length);
        Assert.IsTrue(targets.All(target => target != null), "曲ごとのディスク対象が一度ずつ生成されること");
        int[] targetIds = targets.Select(target => target.GetInstanceID()).ToArray();
        for (int i = 0; i < 20; i++)
        {
            int next = (controller.SelectedIndex + 1) % songCount;
            SongSelectDiscTarget target = targets[next];
            Assert.IsTrue(target.Available, "曲送り" + (i + 1) + "回目: 隣のディスクが選択可能であること");
            Assert.IsTrue(target.TryShoot(), "曲送り" + (i + 1) + "回目: ディスク操作を受け付けること");
            Assert.AreEqual(next, controller.SelectedIndex);
            controller.StopPreview();
            yield return null;
        }
        CollectionAssert.AreEqual(targetIds, targets.Select(target => target.GetInstanceID()).ToArray(),
            "曲送りを繰り返してもディスク対象を作り直さない");
        yield return SceneManager.LoadSceneAsync("Title", LoadSceneMode.Single);
        yield return null;
        Assert.IsTrue(targets.All(target => target == null), "選曲シーンを離れるとディスク対象を解放する");
    }

    [UnityTest]
    public IEnumerator JudgmentFallbackClipsAreReusedAndReleased()
    {
        var sfx = Make<JudgmentSfx>();
        Set(sfx, "defaultClipLoaded", true);
        Set(sfx, "defaultCutClip", null);
        Set(sfx, "defaultMissClip", null);
        var tiers = new[] { JudgmentTier.Perfect, JudgmentTier.Great, JudgmentTier.Good, JudgmentTier.Bad, JudgmentTier.Miss };
        var clips = tiers.Select(sfx.ClipFor).ToArray();
        for (int i = 0; i < tiers.Length; i++) Assert.AreSame(clips[i], sfx.ClipFor(tiers[i]));
        Object.Destroy(sfx.gameObject);
        yield return null;
        yield return null;
        Assert.True(clips.All(c => c == null), "判定音の合成バッファを残さない");
    }

    [UnityTest]
    public IEnumerator LongFallbackCacheIsReleasedWithItsOwner()
    {
        var sfx = Make<LongNoteCutSfx>();
        Set(sfx, "defaultClipLoaded", true);
        Set(sfx, "defaultCutClip", null);
        var clips = Enumerable.Range(0, 10).Select(sfx.ClipForCut).ToArray();
        for (int i = 0; i < clips.Length; i++) Assert.AreSame(clips[i], sfx.ClipForCut(i));
        Object.Destroy(sfx.gameObject);
        yield return null;
        yield return null;
        Assert.True(clips.All(c => c == null), "ロング音のキャッシュも所有者と一緒に解放する");
    }

    [UnityTest]
    public IEnumerator RebuildingGoldFallbackReleasesOldClipsAndThenTheCurrentPair()
    {
        var sfx = Make<GoldNoteSfx>();
        Set(sfx, "defaultClipLoaded", true);
        Set(sfx, "defaultCutClip", null);
        sfx.PlayLuxury();
        var old = GoldClips(sfx);
        sfx.baseFrequency += 300;
        sfx.PlayLuxury();
        var current = GoldClips(sfx);
        yield return null;
        yield return null;
        Assert.True(old.All(c => c == null), "設定変更で作り直した古い合成音を残さない");
        Assert.True(current.All(c => c != null));
        Object.Destroy(sfx.gameObject);
        yield return null;
        yield return null;
        Assert.True(current.All(c => c == null));
    }

    [UnityTest]
    public IEnumerator DestroyingSoundPlayersKeepsBorrowedAndBundledClips()
    {
        borrowed = AudioClip.Create("BorrowedSfxLifetimeTest", 480, 1, 48000, false);
        var bundled = Resources.Load<AudioClip>("Audio/SFX/Saber_NoteCut");
        var judgment = Make<JudgmentSfx>();
        var gold = Make<GoldNoteSfx>();
        var longSound = Make<LongNoteCutSfx>();
        judgment.perfectClip = borrowed;
        gold.cutClip = borrowed;
        longSound.cutClip = borrowed;
        Assert.AreSame(borrowed, judgment.ClipFor(JudgmentTier.Perfect));
        Assert.AreSame(borrowed, gold.ResolveCutClip());
        Assert.AreSame(borrowed, longSound.ClipForCut(0));
        Object.Destroy(judgment.gameObject);
        Object.Destroy(gold.gameObject);
        Object.Destroy(longSound.gameObject);
        yield return null;
        yield return null;
        Assert.True(borrowed != null);
        Assert.True(bundled != null);
        Assert.AreSame(bundled, Resources.Load<AudioClip>("Audio/SFX/Saber_NoteCut"));
    }

    [UnityTest]
    public IEnumerator TitleChimeClipsAreReleasedWithTheTitle()
    {
        yield return SceneManager.LoadSceneAsync("Title");
        yield return null;
        var skin = Object.FindFirstObjectByType<TitleSceneSkin>();
        Assert.NotNull(skin);
        typeof(TitleSceneSkin).GetMethod("PlaySlashChime", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(skin, null);
        var clips = NewGeneratedClips().Where(c => c.name == "beep_880" || c.name == "beep_1319").ToArray();
        Assert.AreEqual(2, clips.Length);
        Object.Destroy(skin.gameObject);
        yield return null;
        yield return null;
        Assert.True(clips.All(c => c == null), "タイトルの開始音を画面終了後に残さない");
    }

    T Make<T>() where T : Component
    {
        var go = new GameObject("SfxLifetimeTest", typeof(AudioSource));
        objects.Add(go);
        return go.AddComponent<T>();
    }
    static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    static AudioClip[] GoldClips(GoldNoteSfx sfx) => new[]
    {
        (AudioClip)typeof(GoldNoteSfx).GetField("firstClip", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(sfx),
        (AudioClip)typeof(GoldNoteSfx).GetField("secondClip", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(sfx)
    };
    AudioClip[] NewGeneratedClips() => Resources.FindObjectsOfTypeAll<AudioClip>()
        .Where(c => !initialClips.Contains(c.GetInstanceID()) &&
            (c.name == "menu_aim_shot" || c.name.StartsWith("beep_") || c.name.StartsWith("buzz_") || c.name.StartsWith("gold_shing"))).ToArray();
}
