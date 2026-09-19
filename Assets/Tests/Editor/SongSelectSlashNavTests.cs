using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// 曲選択の「照準で曲送り」ナビノーツ(SongSelectSlashNav)のテスト。
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
        Assert.AreEqual(CutDirection.Up, nav.UpNote.RequiredDirection, "矢印の向き(見た目)は↑");
        Assert.AreEqual(CutDirection.Down, nav.DownNote.RequiredDirection, "矢印の向き(見た目)は↓");
        Assert.IsFalse(nav.UpNote.IsJudgeable);
        Assert.IsFalse(nav.DownNote.IsJudgeable);
        Assert.IsNotNull(nav.UpNote.transform.Find("Arrow"), "↑ノーツにシェブロン矢印が付く");
        Assert.IsNotNull(nav.DownNote.transform.Find("Arrow"), "↓ノーツにシェブロン矢印が付く");
        Assert.Greater(nav.UpNote.transform.position.y, nav.DownNote.transform.position.y,
            "↑ノーツは↓ノーツより上に置かれる");
    }

    [Test]
    public void ShootUpNote_MovesToPreviousSong_WithWrap()
    {
        var nav = MakeNav(out var ctl);
        Assert.AreEqual(0, ctl.SelectedIndex);

        // 先頭で↑ = 末尾へ回り込む(キーボード↑と同じ)
        Assert.True(nav.TryShoot(true));
        Assert.AreEqual(ctl.SongCount - 1, ctl.SelectedIndex);
    }

    [Test]
    public void ShootDownNote_MovesToNextSong()
    {
        var nav = MakeNav(out var ctl);
        Assert.AreEqual(0, ctl.SelectedIndex);

        Assert.True(nav.TryShoot(false));
        Assert.AreEqual(1, ctl.SelectedIndex);
    }

    [Test]
    public void FastSwingsNeverSelectOrDestroyNavigationNotes()
    {
        var nav = MakeNav(out var ctl);
        nav.UpNote.Cut(nav.UpNote.transform.position, Vector3.down * 100);
        nav.DownNote.Cut(nav.DownNote.transform.position, Vector3.right * 100);
        Assert.AreEqual(0, ctl.SelectedIndex);
        Assert.False(nav.UpNote.IsCut); Assert.False(nav.DownNote.IsCut);
    }

    // ---- 逆側ノーツのクールタイム(1回の振りで「進んで戻る」誤爆の防止) ----

    [Test]
    public void ShootOne_PutsOppositeOnCooldown_AndItsCutDoesNotMoveSelection()
    {
        var nav = MakeNav(out var ctl);
        var up = nav.UpNote;

        Assert.True(nav.TryShoot(false));
        Assert.AreEqual(1, ctl.SelectedIndex);
        Assert.IsTrue(nav.InCooldown, "カット直後はクールタイム中");
        Assert.IsFalse(up.IsJudgeable, "逆側(↑)は判定対象外になる");
        Assert.Less(up.transform.localScale.x, nav.noteScale, "逆側は縮んで『無効』を示す");

        // 巻き込みで↑が切れてしまっても曲は戻らない
        up.Cut(up.transform.position, new Vector3(0f, 9f, 0f));
        Assert.AreEqual(1, ctl.SelectedIndex, "クールタイム中の逆側カットでは曲送りしない");
    }

    [Test]
    public void Cooldown_Expires_ThenOppositeWorksAgain()
    {
        var nav = MakeNav(out var ctl);
        Assert.True(nav.TryShoot(false));
        Assert.AreEqual(1, ctl.SelectedIndex);

        nav.Tick(nav.oppositeCooldown * 0.5f);
        Assert.IsTrue(nav.InCooldown);
        Assert.IsFalse(nav.UpNote.IsJudgeable, "まだクールタイム中");

        nav.Tick(nav.oppositeCooldown * 0.5f + 0.01f);
        Assert.IsFalse(nav.InCooldown, "クールタイム終了");
        Assert.IsTrue(nav.CanShoot(true), "逆側を再び撃てる");
        Assert.AreEqual(nav.noteScale, nav.UpNote.transform.localScale.x, 1e-4f, "見た目も元に戻る");

        Assert.True(nav.TryShoot(true));
        Assert.AreEqual(0, ctl.SelectedIndex, "クールタイム後は前の曲へ戻れる");
    }

    [Test]
    public void RespawnedNote_DuringCooldown_StaysUnjudgeable_UntilCooldownEnds()
    {
        var nav = MakeNav(out _);
        nav.oppositeCooldown = nav.respawnDelay + 1.0f; // 再出現より長いクールタイム

        Assert.True(nav.TryShoot(false));
        nav.Tick(nav.respawnDelay + 0.05f);
        Assert.IsNotNull(nav.DownNote, "再出現している");
        Assert.IsFalse(nav.DownNote.IsJudgeable, "クールタイム中に再出現したノーツは切れない");

        nav.Tick(1.0f);
        Assert.IsFalse(nav.InCooldown);
        Assert.IsTrue(nav.CanShoot(false), "クールタイム終了で発射可能");
        Assert.IsFalse(nav.UpNote.IsJudgeable);
    }

    [Test]
    public void ShotNote_RespawnsAfterDelay_AndWorksAgain()
    {
        var nav = MakeNav(out var ctl);
        Assert.AreEqual(.5f, nav.respawnDelay, 1e-3f, "破片が消える頃に再出現する");

        Assert.True(nav.TryShoot(false));
        Assert.AreEqual(1, ctl.SelectedIndex);
        Assert.IsTrue(nav.DownNote == null, "カット直後は↓ノーツが消えている");

        // 再出現待ちの間は何も起きない
        nav.Tick(nav.respawnDelay * 0.5f);
        Assert.IsTrue(nav.DownNote == null, "待機中はまだ再出現しない");

        nav.Tick(nav.respawnDelay);
        Assert.IsNotNull(nav.DownNote, "遅延後に↓ノーツが再出現する");
        Assert.AreEqual(CutDirection.Down, nav.DownNote.RequiredDirection);
        Assert.IsFalse(nav.DownNote.IsJudgeable);
        Assert.IsTrue(nav.DownNote.DirectionVisualOnly, "再出現したノーツも方向を問わない");

        // 再出現したノーツも曲送りが効く(イベント再購読の確認)
        Assert.True(nav.TryShoot(false));
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
