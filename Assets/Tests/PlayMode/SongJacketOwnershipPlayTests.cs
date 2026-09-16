using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// 実画像の所有権・再利用・破棄を検証する。提供された曲や画像は書き換えない。
public class SongJacketOwnershipPlayTests
{
    const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    SongSelectController controller;
    SongSelectSkin skin;
    string firstDirectory, secondDirectory;
    readonly List<Object> allocated = new List<Object>();

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        string prefix = "__JacketOwnershipTest_" + Guid.NewGuid().ToString("N");
        firstDirectory = Path.Combine(Application.streamingAssetsPath, "Songs", prefix + "A");
        secondDirectory = Path.Combine(Application.streamingAssetsPath, "Songs", prefix + "B");
        WriteCover(firstDirectory, Color.red, 8);
        WriteCover(secondDirectory, Color.blue, 16);
        var go = new GameObject("JacketControllerTest", typeof(RectTransform), typeof(Image));
        controller = go.AddComponent<SongSelectController>();
        controller.enabled = false; // 通常のStartで実曲一覧を構築しない。
        controller.jacketImage = go.GetComponent<Image>();
        var ids = (List<string>)typeof(SongSelectController).GetField("songIds", PrivateInstance).GetValue(controller);
        ids.Add(Path.GetFileName(firstDirectory));
        ids.Add(Path.GetFileName(secondDirectory));
        ids.Add(prefix + "Missing");
        skin = new GameObject("JacketSkinTest").AddComponent<SongSelectSkin>();
        skin.enabled = false;
        typeof(SongSelectSkin).GetField("ctl", PrivateInstance).SetValue(skin, controller);
        yield return null;
    }

    void WriteCover(string directory, Color color, int size)
    {
        Directory.CreateDirectory(directory);
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color[size * size];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
        texture.SetPixels(pixels); texture.Apply();
        File.WriteAllBytes(Path.Combine(directory, "cover.png"), texture.EncodeToPNG());
        Object.Destroy(texture);
    }

    Sprite Select(int index)
    {
        controller.Select(index);
        return Track(controller.jacketImage.sprite);
    }

    Sprite Track(Sprite sprite)
    {
        if (sprite != null) { allocated.Add(sprite.texture); allocated.Add(sprite); }
        return sprite;
    }

    Sprite WheelCover(int index)
    {
        return Track((Sprite)typeof(SongSelectSkin).GetMethod("CoverSprite", PrivateInstance)
            .Invoke(skin, new object[] { index }));
    }

    [UnityTest]
    public IEnumerator RevisitingTracksReusesTheirImages()
    {
        var first = Select(0);
        var second = Select(1);
        Assert.AreEqual(8, first.texture.width);
        Assert.AreEqual(16, second.texture.width);
        Assert.AreNotSame(first, second);
        for (int i = 0; i < 32; i++)
        {
            Assert.AreSame(first, Select(0), "同じ曲へ戻っても新しい画像を割り当てない");
            Assert.AreSame(second, Select(1));
        }
        yield return null;
    }

    [UnityTest]
    public IEnumerator ThumbnailAndDetailShareOneImage()
    {
        var detail = Select(0);
        Assert.AreSame(detail, WheelCover(0), "曲一覧と拡大表示は同じ画像を使う");
        yield return null;
    }

    [UnityTest]
    public IEnumerator MissingCoverDoesNotKeepPreviousTrackImage()
    {
        var existing = Select(0);
        Assert.IsNull(Select(2));
        Assert.IsNull(WheelCover(2));
        Assert.AreNotEqual(Color.white, controller.jacketImage.color);
        Assert.IsTrue(existing != null, "戻る操作に備え、画面内では再利用できる");
        yield return null;
    }

    [UnityTest]
    public IEnumerator UnreadableCoverFallsBackInsteadOfStoppingSelection()
    {
        Select(0);
        using (var locked = new FileStream(Path.Combine(secondDirectory, "cover.png"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.DoesNotThrow(() => Select(1));
            Assert.AreEqual(1, controller.SelectedIndex);
            Assert.IsNull(controller.jacketImage.sprite);
            Assert.IsNull(WheelCover(1));
        }
        yield return null;
    }

    [UnityTest]
    public IEnumerator InvalidImageFallsBackAndTheNextTrackStillLoads()
    {
        File.WriteAllBytes(Path.Combine(firstDirectory, "cover.png"), new byte[] { 0, 1, 2, 3 });
        Assert.IsNull(Select(0));
        Assert.IsNull(WheelCover(0));
        Assert.IsNotNull(Select(1));
        Assert.IsNull(Select(0));
        yield return null;
    }

    [UnityTest]
    public IEnumerator ControllerDoesNotDestroyAnExternallyAssignedImage()
    {
        var texture = new Texture2D(2, 2);
        var sprite = Track(Sprite.Create(texture, new Rect(0, 0, 2, 2), Vector2.one * .5f));
        controller.jacketImage.sprite = sprite;
        Object.Destroy(controller.gameObject);
        yield return null;
        yield return null;
        Assert.IsTrue(sprite != null && texture != null, "自身が読み込んでいない画像の所有権には触れない");
    }

    [UnityTest]
    public IEnumerator RemovingSkinKeepsControllerOwnedImageAlive()
    {
        var detail = Select(0);
        var thumbnail = WheelCover(0);
        Object.Destroy(skin.gameObject);
        yield return null;
        Assert.IsTrue(detail != null && detail.texture != null);
        Assert.AreSame(detail, Select(0));
        Assert.IsTrue(thumbnail != null, "表示スキンは借りた画像を破棄しない");
    }

    [UnityTest]
    public IEnumerator ClosingControllerReleasesSpritesAndTextures()
    {
        var first = Select(0); var firstTexture = first.texture;
        var second = Select(1); var secondTexture = second.texture;
        Object.Destroy(controller.gameObject);
        yield return null;
        yield return null; // OnDestroyから予約されたネイティブ画像の破棄まで待つ。
        Assert.IsTrue(first == null && firstTexture == null, "最初の画像を解放する");
        Assert.IsTrue(second == null && secondTexture == null, "最後に表示した画像も解放する");
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        if (skin != null) Object.Destroy(skin.gameObject);
        if (controller != null) Object.Destroy(controller.gameObject);
        yield return null;
        foreach (var value in allocated) if (value != null) Object.Destroy(value);
        allocated.Clear();
        yield return null;
        DeleteFixture(firstDirectory); DeleteFixture(secondDirectory);
    }

    static void DeleteFixture(string directory)
    {
        if (directory == null) return;
        string root = Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, "Songs")) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(directory);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("__JacketOwnershipTest_", StringComparison.Ordinal))
            throw new InvalidOperationException("テスト素材以外は削除しません。");
        foreach (var suffix in new[] { "cover.png", "cover.png.meta" })
        {
            string file = Path.Combine(full, suffix);
            if (File.Exists(file)) File.Delete(file);
        }
        if (Directory.Exists(full)) Directory.Delete(full); // 予期しないファイルがあれば残して失敗する。
        if (File.Exists(full + ".meta")) File.Delete(full + ".meta");
    }
}
