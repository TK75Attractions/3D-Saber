using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// 運営の知らせが、いちばん長い文でも枠の中に収まり、短い文は今までどおりの大きさで出ることを確かめる
// (ランキング同期の「相手の台が見つかりません(…)」は 30px の1行で画面の幅を超えていた。2026-10-30 全体の点検)。
public class OperatorNoticePlayTests
{
    [UnitySetUp]
    public IEnumerator SetUp()
    {
        RemoveNotices();
        yield return null;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        RemoveNotices();
        yield return null;
    }

    [UnityTest]
    public IEnumerator LongNoticesStayInsideThePanel()
    {
        foreach (string message in new[] { RankingSync.NoPeerNotice, AudioOutputRecovery.ReloadFailedNotice, RankingSync.OneWayNotice("B") })
        {
            OperatorNotice.Show(message);
            yield return null;
            var label = Object.FindFirstObjectByType<OperatorNotice>().Label;
            label.ForceMeshUpdate();
            Assert.AreEqual(message, label.text);
            Assert.LessOrEqual(label.textBounds.size.x, OperatorNotice.MessageSize.x + 1, message + " の幅");
            Assert.LessOrEqual(label.textBounds.size.y, OperatorNotice.MessageSize.y + 1, message + " の高さ");
            Assert.LessOrEqual(label.textInfo.lineCount, 2, message + " は2行まで");
            Assert.GreaterOrEqual(label.fontSize, OperatorNotice.MessageMinimumFontSize, message + " の字の大きさ");
        }
    }

    [UnityTest]
    public IEnumerator AShortNoticeKeepsItsSizeOnOneLine()
    {
        OperatorNotice.Show("プロジェクター表示：ON");
        yield return null;
        var label = Object.FindFirstObjectByType<OperatorNotice>().Label;
        label.ForceMeshUpdate();
        Assert.AreEqual(OperatorNotice.MessageFontSize, label.fontSize, .01f);
        Assert.AreEqual(1, label.textInfo.lineCount);
    }

    static void RemoveNotices()
    {
        foreach (var old in Object.FindObjectsByType<OperatorNotice>(FindObjectsSortMode.None)) Object.Destroy(old.gameObject);
    }
}
