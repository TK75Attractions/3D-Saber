using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;

// 非表示時に描画を止めても、スタッフのキー操作と採取キャンセルが生きていることを確認。
public class PhoneSaberOperatorControlsPlayTests
{
    Keyboard keyboard;
    PhoneSaberOperatorOverlay overlay;
    PhoneSaberLatencyProbe probe;
    GameObject inputObject;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        keyboard = InputSystem.AddDevice<Keyboard>();
        keyboard.MakeCurrent(); // 運営表示は Keyboard.current を読む。
        overlay = Object.FindFirstObjectByType<PhoneSaberOperatorOverlay>();
        probe = Object.FindFirstObjectByType<PhoneSaberLatencyProbe>();
        Assert.IsNotNull(overlay);
        Assert.IsNotNull(probe);
        overlay.enabled = true;
        probe.enabled = true;
        if (Field<bool>(overlay, "visible")) yield return Press(Key.F8);
        if (Field<bool>(probe, "active")) yield return Press(Key.F9);
        yield return null;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        overlay.enabled = true;
        probe.enabled = true;
        if (Field<bool>(overlay, "visible")) yield return Press(Key.F8);
        if (Field<bool>(probe, "active")) yield return Press(Key.F9);
        if (inputObject != null) Object.Destroy(inputObject);
        InputSystem.RemoveDevice(keyboard);
        yield return null;
    }

    [UnityTest]
    public IEnumerator F8OpensAndClosesWhileHiddenGuiIsDisabled()
    {
        Assert.IsFalse(Renderer(overlay).enabled);
        Assert.IsTrue(overlay.enabled, "非表示中も入力の Update は有効");
        yield return Press(Key.F8);
        Assert.IsTrue(Field<bool>(overlay, "visible"));
        Assert.IsTrue(Renderer(overlay).enabled);
        Assert.IsTrue(Renderer(overlay).useGUILayout);
        Assert.IsNotNull(Field<GUIStyle>(overlay, "textStyle"), "有効な描画側の OnGUI が運営表示へ届く");
        yield return Press(Key.F8);
        Assert.IsFalse(Field<bool>(overlay, "visible"));
        Assert.IsFalse(Renderer(overlay).enabled);
    }

    [UnityTest]
    public IEnumerator F7OpensStartsCaptureAndF8Cancels()
    {
        inputObject = new GameObject("OperatorControlsInput");
        inputObject.SetActive(false);
        var input = inputObject.AddComponent<InputPoint>();
        input.port = FreePort();
        do { input.port2 = FreePort(); } while (input.port2 == input.port);
        inputObject.SetActive(true);
        yield return null;
        yield return Press(Key.F7);
        Assert.IsTrue(Field<bool>(overlay, "visible"));
        Assert.IsTrue(Renderer(overlay).enabled);
        Assert.AreEqual(0, Field<int>(overlay, "calibrationCorner"));
        yield return Press(Key.F7);
        Assert.GreaterOrEqual(Field<double>(overlay, "captureStarted"), 0);
        yield return Press(Key.F8);
        Assert.AreEqual(-1, Field<int>(overlay, "calibrationCorner"));
        Assert.AreEqual(-1, Field<double>(overlay, "captureStarted"));
        Assert.IsFalse(Renderer(overlay).enabled);
    }

    [UnityTest]
    public IEnumerator F9TogglesProbeAndDisablingRestoresNormalQueue()
    {
        Assert.IsFalse(Renderer(probe).enabled);
        yield return Press(Key.F9);
        Assert.IsTrue(Field<bool>(probe, "active"));
        Assert.IsTrue(Renderer(probe).enabled);
        Assert.IsFalse(Renderer(probe).useGUILayout);
        Assert.IsNotNull(Field<GUIStyle>(probe, "textStyle"), "継承した OnGUI が遅延表示へ届く");
        yield return Press(Key.F9);
        Assert.IsFalse(Field<bool>(probe, "active"));
        Assert.IsFalse(Renderer(probe).enabled);
        Assert.AreEqual(1, QualitySettings.maxQueuedFrames);
        yield return Press(Key.F9);
        probe.enabled = false;
        Assert.IsFalse(Field<bool>(probe, "active"));
        Assert.IsFalse(Renderer(probe).enabled);
        Assert.AreEqual(1, QualitySettings.maxQueuedFrames);
    }

    [UnityTest]
    public IEnumerator OwnerDisableAndStartupResetStopGui()
    {
        yield return Press(Key.F8);
        overlay.enabled = false;
        Assert.IsFalse(Renderer(overlay).enabled);
        overlay.enabled = true;
        Assert.IsTrue(Renderer(overlay).enabled);
        yield return Press(Key.F9);
        typeof(PhoneSaberOperatorOverlay).GetMethod("CreateAtStartup", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, null);
        typeof(PhoneSaberLatencyProbe).GetMethod("CreateAtStartup", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, null);
        Assert.IsFalse(Field<bool>(overlay, "visible"));
        Assert.IsFalse(Field<bool>(probe, "active"));
        Assert.IsFalse(Renderer(overlay).enabled);
        Assert.IsFalse(Renderer(probe).enabled);
    }

    IEnumerator Press(Key key)
    {
        // batchmode の Editor 更新に仮想キーが先に消費されないよう、
        // Dynamic 入力更新と実際の監視メソッドを同じステップで実行する。
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(key));
        InputSystem.Update();
        typeof(PhoneSaberOperatorOverlay).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(overlay, null);
        typeof(PhoneSaberLatencyProbe).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(probe, null);
        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        InputSystem.Update();
        yield return null;
        yield return null;
    }

    static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);

    static PhoneSaberGuiRenderer Renderer(MonoBehaviour owner) => Field<PhoneSaberGuiRenderer>(owner, "guiRenderer");

    static int FreePort()
    {
        using var socket = new UdpClient(0);
        return ((IPEndPoint)socket.Client.LocalEndPoint).Port;
    }
}
