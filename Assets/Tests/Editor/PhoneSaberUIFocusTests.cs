using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

public class PhoneSaberUIFocusPolicyTests
{
    [TestCase(true, false, false, true)]
    [TestCase(true, true, false, true)]
    [TestCase(false, false, false, false)]
    [TestCase(false, true, false, true)]
    [TestCase(false, false, true, true)]
    [TestCase(false, true, true, true)]
    [TestCase(true, false, true, true)]
    [TestCase(true, true, true, true)]
    public void OnlyPhoneInputBypassesFocusOutsideBatchMode(bool phone, bool focused, bool batch, bool expected)
    {
        Assert.AreEqual(expected, PhoneSaberUIFocus.AllowsInput(phone, focused, batch));
    }
}

public class PhoneSaberUIFocusTests
{
    const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    GameObject root;
    InputPoint previousInput;

    [SetUp]
    public void SetUp()
    {
        previousInput = InputPoint.Instance;
        root = new GameObject("非フォーカスUIの検証");
        root.SetActive(false);
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(root);
        typeof(InputPoint).GetProperty("Instance").SetValue(null, previousInput);
    }

    // ソケットを起動せず、公開済みのiPhone座標の鮮度だけを再現する。
    void SetPhoneInput(bool active)
    {
        var input = root.AddComponent<InputPoint>();
        typeof(InputPoint).GetProperty("LastReceivedTime").SetValue(input,
            active ? Time.realtimeSinceStartupAsDouble : -1000.0);
        typeof(InputPoint).GetProperty("Instance").SetValue(null, input);
    }

    // 選曲・判定調整の残り時間も照準と同じ決まり。iPhone の座標が届いていればウィンドウが裏でも進み、
    // 届いていなければ裏にある間は止まる(10/23 点検: 照準だけ動いて時計が止まり、時間切れが来なかった)。
    [TestCase(false, false, true, true)]
    [TestCase(false, false, false, false)]
    [TestCase(true, false, false, true)]
    [TestCase(false, true, false, true)]
    public void SelectionClockRunsWheneverVisitorsCanAim(bool focused, bool batchMode, bool phoneInput, bool runs)
    {
        Assert.AreEqual(runs, SongSelectSkin.VisitorInputLive(focused, batchMode, phoneInput));
        Assert.AreEqual(PhoneSaberUIFocus.AllowsInput(phoneInput, focused, batchMode), runs, "照準と同じ決まり");
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void FocusLossPreservesPhoneHoldButCancelsMouseOrStaleInput(bool songSelect, bool phoneActive)
    {
        SetPhoneInput(phoneActive);
        MonoBehaviour pointer = songSelect
            ? (MonoBehaviour)root.AddComponent<SongSelectAimPointer>()
            : root.AddComponent<SaberUIPointer>();
        var tracker = (SongSelectAimTracker)pointer.GetType()
            .GetField(songSelect ? "tracker" : "aimTracker", PrivateInstance).GetValue(pointer);
        var target = new object();
        var area = new Rect(0, 0, 100, 100);
        var point = new Vector2(50, 50);
        for (int i = 0; i < 5; i++) tracker.Tick(target, area, point, .1f, true);
        Assert.AreEqual(.5f, tracker.Progress01, .001f);

        pointer.GetType().GetMethod("OnApplicationFocus", PrivateInstance).Invoke(pointer, new object[] { false });

        Assert.AreEqual(phoneActive ? .5f : 0f, tracker.Progress01, .001f,
            "受信中のiPhoneはためを継続し、マウス・受信切れは従来どおり取り消す");
        if (!phoneActive) return;
        for (int i = 0; i < 4; i++) Assert.False(tracker.Tick(target, area, point, .1f, true));
        Assert.True(tracker.Tick(target, area, point, .1f, true), "失焦後も残りの時間で選択できる");
    }
}
