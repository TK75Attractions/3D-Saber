using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// SongSelect.unity を実際にロードして、「切って曲送り」ナビ一式が組み上がるかのスモークテスト。
// (過去に曲選択への3Dノーツ導入で不具合が出た経緯があるため、シーン統合をここで担保する)
public class SongSelectNavSmokeTest
{
    [UnityTest]
    public IEnumerator SongSelectScene_BuildsSlashNavNotes()
    {
        // ナビは実曲2曲以上のときだけ出る仕様。曲が減った環境ではテスト自体を保留する。
        if (SongSelectController.EnumerateSongIds().Count < 2)
        {
            Assert.Ignore("実曲が2曲未満のためナビは生成されない(仕様)");
        }

        yield return SceneManager.LoadSceneAsync("SongSelect", LoadSceneMode.Single);

        // スキンが1フレ遅延で構築されるため、ナビの出現をポーリングで待つ(最長10秒)
        SongSelectSlashNav nav = null;
        float deadline = Time.realtimeSinceStartup + 10f;
        while (Time.realtimeSinceStartup < deadline)
        {
            nav = Object.FindFirstObjectByType<SongSelectSlashNav>();
            if (nav != null && nav.UpNote != null && nav.DownNote != null) break;
            yield return null;
        }

        Assert.IsNotNull(nav, "SongSelectSlashNav がシーンに生成されるはず");
        Assert.IsNotNull(nav.UpNote, "↑ノーツが存在する");
        Assert.IsNotNull(nav.DownNote, "↓ノーツが存在する");
        Assert.AreEqual(CutDirection.Up, nav.UpNote.RequiredDirection);
        Assert.AreEqual(CutDirection.Down, nav.DownNote.RequiredDirection);
        Assert.IsNotNull(nav.UpNote.transform.Find("Arrow"), "矢印マーカーが付いている");
        Assert.IsNotNull(Object.FindFirstObjectByType<SaberCutJudge>(), "メニュー用セーバー判定が存在する");

        // ノーツを UI の手前に描く前提: Canvas がカメラ平面モードへ移っている
        var ctl = Object.FindFirstObjectByType<SongSelectController>();
        Assert.IsNotNull(ctl, "SongSelectController が存在する");
        var canvas = ctl.GetComponentInParent<Canvas>();
        Assert.IsNotNull(canvas, "Canvas が存在する");
        Assert.AreEqual(RenderMode.ScreenSpaceCamera, canvas.renderMode, "Canvas はカメラ平面モード");
        Assert.IsNotNull(canvas.worldCamera, "Canvas にメインカメラが割り付いている");
    }
}
