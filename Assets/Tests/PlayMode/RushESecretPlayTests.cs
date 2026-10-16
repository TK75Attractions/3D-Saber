using System.Collections;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public class RushESecretPlayTests
{
    [UnityTest]
    public IEnumerator MouseMovementWithoutAnyButtonRevealsEAfterTenCuts()
    {
        if (!File.Exists(Path.Combine(Application.streamingAssetsPath, "Songs", "RushE", "audio.wav"))) Assert.Ignore("文化祭用ローカル音源が必要です。");
        var originalSettings = InputSystem.settings;
        var settings = Object.Instantiate(originalSettings);
        InputSystem.settings = settings;
        settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
        settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
        var mouse = InputSystem.AddDevice<Mouse>();
        SongSelectController ctl = null;
        try
        {
            InputSystem.QueueStateEvent(mouse, new MouseState { position = Vector2.zero });
            yield return SceneManager.LoadSceneAsync("SongSelect", LoadSceneMode.Single);
            ctl = Object.FindFirstObjectByType<SongSelectController>();
            var skin = Object.FindFirstObjectByType<SongSelectSkin>();
            float deadline = Time.realtimeSinceStartup + 15;
            while (!skin.IsReady && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(skin.IsReady);
            ctl.Select(Enumerable.Range(0, ctl.SongCount).First(i => ctl.SongIdAt(i) == "RushE"));
            ctl.SetDifficulty(2);
            deadline = Time.realtimeSinceStartup + 5;
            while ((ScreenTransition.IsBusy || !skin.DiscsSettled) && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(skin.DiscsSettled); Assert.IsFalse(ScreenTransition.IsBusy);
            yield return new WaitForSecondsRealtime(.2f);
            Assert.IsFalse(InputPoint.Instance.IsRecentlyActive(.15) || InputPoint.Instance.IsRecentlyActive2(.15));
            var secret = Object.FindFirstObjectByType<RushESecret>();
            var aim = Object.FindFirstObjectByType<SongSelectAimPointer>();
            Assert.IsTrue(aim.enabled);
            Canvas.ForceUpdateCanvases();
            Rect area = skin.SelectedDiscScreenRect;
            float radius = Mathf.Min(area.width, area.height) * .5f;
            Vector2 left = area.center + Vector2.left * radius * 1.3f;
            Vector2 right = area.center + Vector2.right * radius * 1.3f;
            InputSystem.QueueStateEvent(mouse, new MouseState { position = left });
            yield return null;
            yield return new WaitForSecondsRealtime(.2f);
            Assert.Zero(ctl.RushESecret.Hits, "置いただけでは切断しない");
            for (int cut = 0; cut < 10; cut++)
            {
                // ボタンを一度も押さず、実際のInput System更新で盤を往復する。
                for (int step = 1; step <= 10; step++)
                {
                    Vector2 point = Vector2.Lerp(cut % 2 == 0 ? left : right, cut % 2 == 0 ? right : left, step / 10f);
                    InputSystem.QueueStateEvent(mouse, new MouseState { position = point });
                    yield return null;
                }
                yield return new WaitForSecondsRealtime(.15f);
                Assert.IsFalse(mouse.leftButton.isPressed || mouse.rightButton.isPressed || mouse.middleButton.isPressed);
                Assert.AreEqual(cut + 1, ctl.RushESecret.Hits, "押していないマウスの1往路を1回だけ数える");
                Assert.AreEqual("SongSelect", SceneManager.GetActiveScene().name);
                if (cut < 9)
                {
                    Assert.IsFalse(ctl.RushESecret.Transitioning);
                    Assert.IsFalse(secret.Curtain.gameObject.activeSelf);
                    Assert.IsFalse(secret.GetComponent<AudioSource>().isPlaying);
                    Assert.IsNull(GameObject.Find("RushEHitRing"));
                    Assert.IsNull(GameObject.Find("RushEOnlyDifficulty"));
                }
            }
            Assert.Zero(aim.ShotCount, "切る途中でかざす操作が発火しない");
            yield return new WaitForSecondsRealtime(RushESecretState.TransitionSeconds + .1f);
            Assert.AreEqual("E", ctl.CurrentDifficultyName);
            Assert.IsTrue(ctl.RushESecret.Unlocked); Assert.IsFalse(ctl.RushESecret.Transitioning);
        }
        finally
        {
            if (ctl != null) ctl.StopPreview();
            InputSystem.RemoveDevice(mouse);
            InputSystem.settings = originalSettings;
            Object.DestroyImmediate(settings);
        }
    }

    [UnityTest]
    public IEnumerator CameraCutsRevealEWithoutClicksOrHintsBeforeTheTenthCut()
    {
        if (!File.Exists(Path.Combine(Application.streamingAssetsPath, "Songs", "RushE", "audio.wav"))) Assert.Ignore("文化祭用ローカル音源が必要です。");
        yield return SceneManager.LoadSceneAsync("SongSelect", LoadSceneMode.Single);
        var ctl = Object.FindFirstObjectByType<SongSelectController>();
        var skin = Object.FindFirstObjectByType<SongSelectSkin>();
        float deadline = Time.realtimeSinceStartup + 15;
        while (!skin.IsReady && Time.realtimeSinceStartup < deadline) yield return null;
        Assert.IsTrue(skin.IsReady);
        int rush = Enumerable.Range(0, ctl.SongCount).First(i => ctl.SongIdAt(i) == "RushE");
        ctl.Select(rush); ctl.SetDifficulty(2);
        deadline = Time.realtimeSinceStartup + 5;
        while ((ScreenTransition.IsBusy || !skin.DiscsSettled) && Time.realtimeSinceStartup < deadline) yield return null;
        Assert.IsTrue(skin.DiscsSettled); Assert.IsFalse(ScreenTransition.IsBusy);
        var input = InputPoint.Instance;
        Assert.IsTrue(input.ReceiverAlive && input.ReceiverAlive2);
        var secret = Object.FindFirstObjectByType<RushESecret>();
        var aim = Object.FindFirstObjectByType<SongSelectAimPointer>();
        Assert.IsTrue(aim.enabled, "通常のかざす操作も動かしたまま検証する");
        var sound = secret.GetComponent<AudioSource>();
        float oldSensitivity = input.sensitivity;
        var calibration = typeof(InputPoint).GetField("positionCalibrationEnabled", BindingFlags.Instance | BindingFlags.NonPublic);
        bool oldCalibration = (bool)calibration.GetValue(input);
        input.sensitivity = 1; calibration.SetValue(input, false);
        using var sender = new UdpClient();
        try
        {
            Canvas.ForceUpdateCanvases();
            Rect area = skin.SelectedDiscScreenRect;
            float radius = Mathf.Min(area.width, area.height) * .5f;
            Vector2 left = area.center + Vector2.left * radius * 1.3f;
            Vector2 right = area.center + Vector2.right * radius * 1.3f;
            // 赤・青の受信口へ実際の4端点座標を送る。クリックイベントや解除APIは呼ばない。
            for (int i = 0; i < 9; i++)
            {
                bool blue = i >= 4 && i < 8;
                bool both = i == 8;
                yield return SendCameraPoint(sender, input, left, blue, both);
                yield return new WaitForSecondsRealtime(.025f);
                yield return SendCameraPoint(sender, input, right, blue, both);
                yield return new WaitForSecondsRealtime(.16f);
                Assert.AreEqual(i + 1, ctl.RushESecret.Hits, "両手同時も1振りとして数える");
                Assert.IsFalse(ctl.RushESecret.Unlocked);
                Assert.IsFalse(ctl.RushESecret.Transitioning);
                Assert.IsFalse(secret.Curtain.gameObject.activeSelf);
                Assert.IsFalse(sound.isPlaying, "途中の打音から秘密が分からない");
                Assert.IsNull(GameObject.Find("RushEHitRing"), "途中の光やカウント表示を作らない");
                Assert.IsNull(GameObject.Find("RushEOnlyDifficulty"));
                Assert.AreEqual("Hard", ctl.CurrentDifficultyName);
                Assert.AreEqual("SongSelect", SceneManager.GetActiveScene().name);
            }
            Assert.AreEqual(0, aim.ShotCount, "切る動作で通常の開始・曲送りを誤発火しない");
            yield return SendCameraPoint(sender, input, left, false, false);
            yield return new WaitForSecondsRealtime(.025f);
            yield return SendCameraPoint(sender, input, right, false, false);
            Assert.AreEqual(10, ctl.RushESecret.Hits);
            Assert.IsTrue(ctl.RushESecret.Transitioning);
            Assert.IsTrue(secret.Curtain.gameObject.activeSelf);
            yield return new WaitForSecondsRealtime(RushESecretState.TransitionSeconds + .1f);
            Assert.IsTrue(ctl.RushESecret.Unlocked); Assert.IsFalse(ctl.RushESecret.Transitioning);
            Assert.AreEqual("E", ctl.CurrentDifficultyName);
            Assert.IsTrue(ctl.startButton.interactable);
            ctl.Select((rush + 1) % ctl.SongCount);
            Assert.AreEqual(0, ctl.RushESecret.Hits); Assert.IsFalse(ctl.RushESecret.Unlocked);
        }
        finally
        {
            input.sensitivity = oldSensitivity; calibration.SetValue(input, oldCalibration);
            ctl.StopPreview();
        }
    }

    static IEnumerator SendCameraPoint(UdpClient sender, InputPoint input, Vector2 pixels, bool blue, bool both)
    {
        double previousRed = input.LastReceivedTime, previousBlue = input.LastReceivedTime2;
        Vector2 point = new Vector2(pixels.x / Screen.width, pixels.y / Screen.height) * 2 - Vector2.one;
        string payload = System.FormattableString.Invariant($"{point.x - .02f},{point.y - .02f},{point.x + .02f},{point.y + .02f}");
        byte[] bytes = Encoding.ASCII.GetBytes(payload);
        if (!blue || both) sender.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, input.port));
        if (blue || both) sender.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, input.port2));
        float deadline = Time.realtimeSinceStartup + 1;
        while ((((!blue || both) && input.LastReceivedTime <= previousRed) || ((blue || both) && input.LastReceivedTime2 <= previousBlue))
            && Time.realtimeSinceStartup < deadline) yield return null;
        if (!blue || both) Assert.Greater(input.LastReceivedTime, previousRed, "赤のUDP座標がUpdateまで届く");
        if (blue || both) Assert.Greater(input.LastReceivedTime2, previousBlue, "青のUDP座標がUpdateまで届く");
        yield return null;
    }

    [UnityTest]
    public IEnumerator JacketClicksRevealOnlyEThenSongChangeRestoresNormalMenu()
    {
        if (!File.Exists(Path.Combine(Application.streamingAssetsPath, "Songs", "RushE", "audio.wav"))) Assert.Ignore("文化祭用ローカル音源が必要です。");
        yield return SceneManager.LoadSceneAsync("SongSelect", LoadSceneMode.Single);
        var ctl = Object.FindFirstObjectByType<SongSelectController>();
        var skin = Object.FindFirstObjectByType<SongSelectSkin>();
        float deadline = Time.realtimeSinceStartup + 15;
        while (!skin.IsReady && Time.realtimeSinceStartup < deadline) yield return null;
        Assert.IsTrue(skin.IsReady);
        Object.FindFirstObjectByType<SongSelectAimPointer>().enabled = false;
        int rush = Enumerable.Range(0, ctl.SongCount).First(i => ctl.SongIdAt(i) == "RushE");
        ctl.Select(rush); ctl.SetDifficulty(2);
        yield return new WaitForSecondsRealtime(.8f);
        while (ScreenTransition.IsBusy || !skin.DiscsSettled) yield return null;
        for (int i = 0; i < 9; i++)
        {
            ctl.startButton.onClick.Invoke(); yield return new WaitForSecondsRealtime(.25f);
            Assert.IsFalse(Object.FindFirstObjectByType<RushESecret>().GetComponent<AudioSource>().isPlaying);
            Assert.IsNull(GameObject.Find("RushEHitRing"));
        }
        Assert.AreEqual(9, ctl.RushESecret.Hits); Assert.IsFalse(ctl.RushESecret.Unlocked);
        Assert.AreEqual("SongSelect", SceneManager.GetActiveScene().name, "ジャケットを切っても開始しない");
        ctl.startButton.onClick.Invoke(); Assert.IsTrue(ctl.RushESecret.Transitioning);
        double remaining = skin.RemainingSeconds; skin.TickCountdown(500);
        Assert.AreEqual(remaining, skin.RemainingSeconds); ctl.StartGame();
        Assert.AreEqual("SongSelect", SceneManager.GetActiveScene().name);
        yield return new WaitForSecondsRealtime(.4f);
        var curtain = Object.FindFirstObjectByType<RushETransitionGraphic>();
        Assert.IsNotNull(curtain, "切替中にタイルが有効になる");
        Canvas.ForceUpdateCanvases();
        Assert.IsNotNull(curtain.canvasRenderer, "タイルの描画コンポーネントが必要");
        Assert.Greater(curtain.canvasRenderer.GetMesh().vertexCount, 0, "四角形の描画メッシュが作られる");
        yield return new WaitForSecondsRealtime(1.6f);
        Assert.IsTrue(ctl.RushESecret.Unlocked); Assert.IsFalse(ctl.RushESecret.Transitioning);
        Assert.AreEqual("E", ctl.CurrentDifficultyName); Assert.AreEqual(10, ctl.CurrentDifficultyDisplayLevel());
        Assert.IsTrue(skin.Background.SecretTheme); Assert.IsNotNull(GameObject.Find("RushEOnlyDifficulty"));
        Assert.IsTrue(ctl.difficultyButtons.All(b => !b.gameObject.activeSelf)); Assert.IsTrue(ctl.startButton.interactable);
        ctl.ChangeDifficulty(1); ctl.SetDifficulty(0); Assert.AreEqual("E", ctl.CurrentDifficultyName);
        ctl.Select((rush + 1) % ctl.SongCount);
        Assert.IsFalse(ctl.RushESecret.Unlocked); Assert.AreEqual(0, ctl.RushESecret.Hits); Assert.IsFalse(skin.Background.SecretTheme);
        Assert.IsTrue(ctl.difficultyButtons.All(b => b.gameObject.activeSelf));
        ctl.Select(rush); Assert.AreEqual("Hard", ctl.CurrentDifficultyName); Assert.AreEqual(0, ctl.RushESecret.Hits);
        ctl.StopPreview();
    }
}
