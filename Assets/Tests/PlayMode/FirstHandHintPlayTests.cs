using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class FirstHandHintPlayTests
{
    // 3・2・1 を数えている途中で部品を無効にされたら(OnDestroy は走らない)、別の表示の予告も残さない。
    [UnityTest]
    public IEnumerator DisablingTheCountdownMidwayRemovesTheHint()
    {
        var root = new GameObject("HintDisable", typeof(RectTransform));
        try
        {
            var countdown = root.AddComponent<GameStartCountdown>();
            var chart = new ChartData();
            chart.notes.Add(new NoteData { color = "red", time = 4000 });
            countdown.Begin(120, "normal", 0, 1, 4);
            countdown.ConfigureFirstHandHint(chart, 4, false, false);
            yield return null;
            Assert.IsNotNull(GameObject.Find("FirstHandHint"), "数えている間は予告がある");
            countdown.enabled = false;
            yield return null;
            Assert.IsNull(GameObject.Find("FirstHandHint"), "途中で無効にしたら予告も消す");
        }
        finally
        {
            Object.Destroy(root);
            var left = GameObject.Find("FirstHandHint");
            if (left != null) Object.Destroy(left);
        }
    }
}
