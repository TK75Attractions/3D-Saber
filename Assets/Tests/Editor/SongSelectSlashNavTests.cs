using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// 曲選択の「切って曲送り」ナビノーツ(SongSelectSlashNav)のテスト。
// EditMode では Awake が呼ばれないため Init() を直接呼ぶ(確立済みパターン)。
// SongSelectController.Populate は StreamingAssets の実曲を読む(2曲以上ある前提)。
public class SongSelectSlashNavTests
{
    private readonly List<GameObject> created = new List<GameObject>();

    [TearDown]
    public void Cleanup()
    {
        foreach (var go in created)
        {
            if (go != null) Object.DestroyImmediate(go);
        }
        created.Clear();
        // カットで生じたノーツ本体(未スライス時は非アクティブ化)と破片を掃除
        foreach (var n in Object.FindObjectsByType<CuttableNote>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (n != null) Object.DestroyImmediate(n.gameObject);
        }
        foreach (var p in Object.FindObjectsByType<SlicePieceDecay>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (p != null) Object.DestroyImmediate(p.gameObject);
        }
        var nav = Object.FindFirstObjectByType<SongSelectSlashNav>(FindObjectsInactive.Include);
        if (nav != null) Object.DestroyImmediate(nav.gameObject);
    }

    private SongSelectSlashNav MakeNav(out SongSelectController ctl)
    {
        var ctlGo = new GameObject("ctl");
        created.Add(ctlGo);
        ctl = ctlGo.AddComponent<SongSelectController>();
        ctl.Populate();
        Assert.GreaterOrEqual(ctl.SongCount, 2, "テストには実曲が2曲以上必要(StreamingAssets/Songs)");
        ctl.Select(0);

        var camGo = new GameObject("navCam");
        created.Add(camGo);
        camGo.transform.position = new Vector3(0f, 0f, -10f);
        var cam = camGo.AddComponent<Camera>();

        var navGo = new GameObject("nav");
        created.Add(navGo);
        var nav = navGo.AddComponent<SongSelectSlashNav>();
        nav.Init(ctl, cam);
        return nav;
    }

    [Test]
    public void Init_SpawnsUpAndDownNotes_WithArrows()
    {
        var nav = MakeNav(out _);

        Assert.IsNotNull(nav.UpNote, "↑ノーツが生成される");
        Assert.IsNotNull(nav.DownNote, "↓ノーツが生成される");
        Assert.AreEqual(CutDirection.Up, nav.UpNote.RequiredDirection);
        Assert.AreEqual(CutDirection.Down, nav.DownNote.RequiredDirection);
        Assert.IsTrue(nav.UpNote.IsJudgeable);
        Assert.IsTrue(nav.DownNote.IsJudgeable);
        Assert.IsNotNull(nav.UpNote.transform.Find("Arrow"), "↑ノーツにシェブロン矢印が付く");
        Assert.IsNotNull(nav.DownNote.transform.Find("Arrow"), "↓ノーツにシェブロン矢印が付く");
        Assert.Greater(nav.UpNote.transform.position.y, nav.DownNote.transform.position.y,
            "↑ノーツは↓ノーツより上に置かれる");
    }

    [Test]
    public void CutUpNote_MovesToPreviousSong_WithWrap()
    {
        var nav = MakeNav(out var ctl);
        Assert.AreEqual(0, ctl.SelectedIndex);

        // 先頭で↑ = 末尾へ回り込む(キーボード↑と同じ)
        nav.UpNote.Cut(nav.UpNote.transform.position, new Vector3(0f, 9f, 0f));
        Assert.AreEqual(ctl.SongCount - 1, ctl.SelectedIndex);
    }

    [Test]
    public void CutDownNote_MovesToNextSong()
    {
        var nav = MakeNav(out var ctl);
        Assert.AreEqual(0, ctl.SelectedIndex);

        nav.DownNote.Cut(nav.DownNote.transform.position, new Vector3(0f, -9f, 0f));
        Assert.AreEqual(1, ctl.SelectedIndex);
    }

    [Test]
    public void OppositeSwing_DoesNotTrigger()
    {
        var nav = MakeNav(out var ctl);

        // ↑ノーツに対する下振りは逆方向拒否で無反応(誤爆防止)
        nav.UpNote.Cut(nav.UpNote.transform.position, new Vector3(0f, -9f, 0f));
        Assert.AreEqual(0, ctl.SelectedIndex, "逆方向スイングでは曲が動かない");
        Assert.IsNotNull(nav.UpNote, "ノーツは無傷で残る");
        Assert.IsFalse(nav.UpNote.IsCut);
    }

    [Test]
    public void CutNote_RespawnsAfterDelay_AndWorksAgain()
    {
        var nav = MakeNav(out var ctl);

        nav.DownNote.Cut(nav.DownNote.transform.position, new Vector3(0f, -9f, 0f));
        Assert.AreEqual(1, ctl.SelectedIndex);
        Assert.IsTrue(nav.DownNote == null, "カット直後は↓ノーツが消えている");

        // 再出現待ちの間は何も起きない
        nav.Tick(nav.respawnDelay * 0.5f);
        Assert.IsTrue(nav.DownNote == null, "待機中はまだ再出現しない");

        nav.Tick(nav.respawnDelay);
        Assert.IsNotNull(nav.DownNote, "遅延後に↓ノーツが再出現する");
        Assert.AreEqual(CutDirection.Down, nav.DownNote.RequiredDirection);
        Assert.IsTrue(nav.DownNote.IsJudgeable);

        // 再出現したノーツも曲送りが効く(イベント再購読の確認)
        nav.DownNote.Cut(nav.DownNote.transform.position, new Vector3(0f, -9f, 0f));
        Assert.AreEqual(2 % ctl.SongCount, ctl.SelectedIndex);
    }

    [Test]
    public void NextIndex_WrapsBothDirections()
    {
        Assert.AreEqual(2, SongSelectSlashNav.NextIndex(0, -1, 3), "先頭から↑で末尾へ");
        Assert.AreEqual(0, SongSelectSlashNav.NextIndex(2, +1, 3), "末尾から↓で先頭へ");
        Assert.AreEqual(1, SongSelectSlashNav.NextIndex(0, +1, 3));
        Assert.AreEqual(0, SongSelectSlashNav.NextIndex(0, +1, 1), "1曲ならその場に留まる");
        Assert.AreEqual(-1, SongSelectSlashNav.NextIndex(0, +1, 0), "0曲は選択なし");
    }
}
