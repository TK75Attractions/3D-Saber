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
    public IEnumerator RepeatedSongNavigationReusesOneTickAndReleasesIt()
    {
        yield return SceneManager.LoadSceneAsync("SongSelect");
        SongSelectSlashNav nav = null;
        double deadline = Time.realtimeSinceStartupAsDouble + 30;
        while (true)
        {
            nav = Object.FindFirstObjectByType<SongSelectSlashNav>();
            if (nav != null && nav.DownNote != null) break;
            if (Time.realtimeSinceStartupAsDouble >= deadline) break;
            yield return null;
        }
        Assert.NotNull(nav);
        var controller = Object.FindFirstObjectByType<SongSelectController>();
        foreach (var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None)) judge.enabled = false;
        nav.enabled = false;
        int start = controller.SelectedIndex;
        for (int i = 0; i < 20; i++)
        {
            nav.DownNote.Cut(nav.DownNote.transform.position, Vector3.down * 8, CutDirection.Down, SaberHand.Any);
            controller.StopPreview();
            nav.Tick(2.1f);
        }
        Assert.AreEqual((start + 20) % controller.SongCount, controller.SelectedIndex);
        var clips = NewGeneratedClips().Where(c => c.name == "beep_660").ToArray();
        Assert.AreEqual(1, clips.Length, "20回の曲送りで同じ音のバッファを20個作らない");
        Assert.AreEqual(4410, clips[0].samples);
        Object.Destroy(nav.gameObject);
        yield return null;
        yield return null;
        Assert.True(clips[0] == null, "所有者の破棄で合成音も解放する");
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
            (c.name.StartsWith("beep_") || c.name.StartsWith("buzz_") || c.name.StartsWith("gold_shing"))).ToArray();
}
