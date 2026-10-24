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
    // 剣の位置を UDP で送ったテストのあと、シーンをまたいで残る受け口(InputPoint)に「位置が届いた」記録を残さない。
    // 残ると、次のクラスの本編が「カメラの台なのに位置が来ない」とみなして曲の頭で止める(2026-10-22)。次のシーンが作り直す。
    [UnityTearDown]
    public IEnumerator ForgetCameraInput()
    {
        foreach (var input in Object.FindObjectsByType<InputPoint>(FindObjectsSortMode.None)) Object.Destroy(input.gameObject);
        yield return null;
    }

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
        byte[] bytes = Payload(pixels);
        if (!blue || both) sender.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, input.port));
        if (blue || both) sender.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, input.port2));
        float deadline = Time.realtimeSinceStartup + 1;
        while ((((!blue || both) && input.LastReceivedTime <= previousRed) || ((blue || both) && input.LastReceivedTime2 <= previousBlue))
            && Time.realtimeSinceStartup < deadline) yield return null;
        if (!blue || both) Assert.Greater(input.LastReceivedTime, previousRed, "赤のUDP座標がUpdateまで届く");
        if (blue || both) Assert.Greater(input.LastReceivedTime2, previousBlue, "青のUDP座標がUpdateまで届く");
        yield return null;
    }

    // 画面ピクセルを、カメラが送る4端点(短い棒)の文字列にする。
    static byte[] Payload(Vector2 pixels)
    {
        Vector2 point = new Vector2(pixels.x / Screen.width, pixels.y / Screen.height) * 2 - Vector2.one;
        return Encoding.ASCII.GetBytes(System.FormattableString.Invariant($"{point.x - .02f},{point.y - .02f},{point.x + .02f},{point.y + .02f}"));
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

    // 2026-10-24: ElDorado も E と同じく、ジャケットを10回切ると隠し譜面 G に移る。金色の札・幕・背景で、
    // 試聴は長い版の最終ピークから。始めると長い版(3分43秒)の音源で、長い版の Hard を全部金にした譜面を流す。
    [UnityTest]
    public IEnumerator ElDoradoJacketCutsRevealGoldGAndStartTheLongAllGoldChart()
    {
        yield return SceneManager.LoadSceneAsync("SongSelect", LoadSceneMode.Single);
        var ctl = Object.FindFirstObjectByType<SongSelectController>();
        var skin = Object.FindFirstObjectByType<SongSelectSkin>();
        float deadline = Time.realtimeSinceStartup + 15;
        while (!skin.IsReady && Time.realtimeSinceStartup < deadline) yield return null;
        Assert.IsTrue(skin.IsReady);
        Object.FindFirstObjectByType<SongSelectAimPointer>().enabled = false;
        int eldorado = Enumerable.Range(0, ctl.SongCount).First(i => ctl.SongIdAt(i) == "ElDorado");
        ctl.Select(eldorado); ctl.SetDifficulty(2);
        yield return new WaitForSecondsRealtime(.8f);
        while (ScreenTransition.IsBusy || !skin.DiscsSettled) yield return null;
        for (int i = 0; i < 9; i++) { ctl.startButton.onClick.Invoke(); yield return new WaitForSecondsRealtime(.25f); }
        Assert.AreEqual(9, ctl.RushESecret.Hits); Assert.IsFalse(ctl.RushESecret.Unlocked);
        Assert.AreEqual("SongSelect", SceneManager.GetActiveScene().name, "ジャケットを切っても開始しない");
        ctl.startButton.onClick.Invoke(); Assert.IsTrue(ctl.RushESecret.Transitioning);
        yield return new WaitForSecondsRealtime(.4f);
        var curtain = Object.FindFirstObjectByType<RushETransitionGraphic>();
        Assert.IsNotNull(curtain);
        Assert.AreEqual(RushESecretState.ElDoradoG.TileDeep, curtain.DeepColor, "幕の四角も金");
        yield return new WaitForSecondsRealtime(1.6f);
        Assert.IsTrue(ctl.RushESecret.Unlocked); Assert.IsFalse(ctl.RushESecret.Transitioning);
        Assert.AreEqual("G", ctl.CurrentDifficultyName);
        Assert.IsTrue(skin.Background.SecretTheme);
        var letter = GameObject.Find("RushEOnlyDifficulty").transform.Find("Letter").GetComponent<TMPro.TextMeshProUGUI>();
        Assert.AreEqual("G", letter.text); Assert.AreEqual(UISkinPalette.NoteGold, letter.color, "札は金の G");
        Assert.IsTrue(ctl.difficultyButtons.All(b => !b.gameObject.activeSelf)); Assert.IsTrue(ctl.startButton.interactable);
        deadline = Time.realtimeSinceStartup + 10;
        while (!(ctl.ChartPreview != null && ctl.ChartPreview.IsPlaying) && Time.realtimeSinceStartup < deadline) yield return null;
        Assert.IsTrue(ctl.ChartPreview.IsPlaying, "G の試聴が鳴る");
        Assert.AreEqual(167.4, ctl.ChartPreview.Window.Start, 1.5, "長い版の最終ピークから試聴(短い版は144秒で終わる)");
        ctl.StartGame();
        GamePlayManager manager = null;
        deadline = Time.realtimeSinceStartup + 20;
        while ((manager == null || !manager.songPlayer.IsScheduled) && Time.realtimeSinceStartup < deadline)
        {
            yield return null;
            if (SceneManager.GetActiveScene().name == "Game") manager = Object.FindFirstObjectByType<GamePlayManager>();
        }
        Assert.NotNull(manager, "G で本編が始まる");
        Assert.AreEqual("G", GameSession.SelectedDifficulty);
        Assert.AreEqual(392, manager.noteSpawner.TotalNoteCount, "長い版の Hard を全部流す");
        Assert.IsTrue(manager.noteSpawner.CurrentChart.notes.All(n => n.color == "gold"), "全部金");
        Assert.Greater(manager.songPlayer.Duration, 220, "長い版(3分43秒)の音源で鳴らす");
        Assert.AreEqual(UISkinPalette.NoteGold, GameHUDSkin.DifficultyColor(GameSession.SelectedDifficulty));
        yield return SceneManager.LoadSceneAsync("Title", LoadSceneMode.Single);
    }

    // 音の出力先が変わっても(AudioOutputRecovery、2026-10-22)、選曲はそのままで、解除した E のまま。
    [UnityTest]
    public IEnumerator AudioOutputChangeKeepsTheRevealedE()
    {
        if (!File.Exists(Path.Combine(Application.streamingAssetsPath, "Songs", "RushE", "audio.wav"))) Assert.Ignore("文化祭用ローカル音源が必要です。");
        yield return SceneManager.LoadSceneAsync("SongSelect", LoadSceneMode.Single);
        var ctl = Object.FindFirstObjectByType<SongSelectController>();
        var skin = Object.FindFirstObjectByType<SongSelectSkin>();
        float deadline = Time.realtimeSinceStartup + 15;
        while (!skin.IsReady && Time.realtimeSinceStartup < deadline) yield return null;
        Object.FindFirstObjectByType<SongSelectAimPointer>().enabled = false;
        int rush = Enumerable.Range(0, ctl.SongCount).First(i => ctl.SongIdAt(i) == "RushE");
        ctl.Select(rush); ctl.SetDifficulty(2);
        yield return new WaitForSecondsRealtime(.8f);
        while (ScreenTransition.IsBusy || !skin.DiscsSettled) yield return null;
        for (int i = 0; i < 10; i++) { ctl.startButton.onClick.Invoke(); yield return new WaitForSecondsRealtime(.25f); }
        yield return new WaitForSecondsRealtime(2.2f);
        Assert.AreEqual("E", ctl.CurrentDifficultyName);
        int recoveries = AudioOutputRecovery.RecoveryCount;
        Assert.True(AudioSettings.Reset(AudioSettings.GetConfiguration()));
        deadline = Time.realtimeSinceStartup + 10;
        while (AudioOutputRecovery.RecoveryCount == recoveries && Time.realtimeSinceStartup < deadline) yield return null;
        Assert.AreEqual(AudioOutputRecovery.Action.InPlace, AudioOutputRecovery.LastAction);
        Assert.False(ScreenTransition.IsBusy, "選曲を作り直さない");
        Assert.AreSame(ctl, Object.FindFirstObjectByType<SongSelectController>());
        Assert.AreEqual("RushE", ctl.SongIdAt(ctl.SelectedIndex));
        Assert.IsTrue(ctl.RushESecret.Unlocked); Assert.IsFalse(ctl.RushESecret.Transitioning);
        Assert.AreEqual("E", ctl.CurrentDifficultyName, "E のまま");
        ctl.StopPreview();
    }

    // 判定調整を往復しても E のまま(2026-10-22 くり返し3回目)。タイトルへ抜けたら持ち越さない。
    [UnityTest]
    public IEnumerator CalibrationRoundTripKeepsTheRevealedE()
    {
        if (!File.Exists(Path.Combine(Application.streamingAssetsPath, "Songs", "RushE", "audio.wav"))) Assert.Ignore("文化祭用ローカル音源が必要です。");
        yield return SceneManager.LoadSceneAsync("SongSelect", LoadSceneMode.Single);
        var ctl = Object.FindFirstObjectByType<SongSelectController>();
        var skin = Object.FindFirstObjectByType<SongSelectSkin>();
        float deadline = Time.realtimeSinceStartup + 15;
        while (!skin.IsReady && Time.realtimeSinceStartup < deadline) yield return null;
        Object.FindFirstObjectByType<SongSelectAimPointer>().enabled = false;
        int rush = Enumerable.Range(0, ctl.SongCount).First(i => ctl.SongIdAt(i) == "RushE");
        ctl.Select(rush); ctl.SetDifficulty(2);
        yield return new WaitForSecondsRealtime(.8f);
        while (ScreenTransition.IsBusy || !skin.DiscsSettled) yield return null;
        for (int i = 0; i < 10; i++) { ctl.startButton.onClick.Invoke(); yield return new WaitForSecondsRealtime(.25f); }
        yield return new WaitForSecondsRealtime(2.2f);
        Assert.AreEqual("E", ctl.CurrentDifficultyName);
        for (int exit = 0; exit < 2; exit++)
        {
            SongSelectSkin.EnterCalibration();
            yield return ScreenTransitionPlayTests.WaitForTransition();
            Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
            var calibration = Object.FindFirstObjectByType<GamePlayManager>().Calibration;
            Assert.NotNull(calibration);
            foreach (var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None)) judge.autonomous = false;
            if (exit == 1)
            {
                calibration.DiscardAndExitTo("Title");
                yield return ScreenTransitionPlayTests.WaitForTransition();
                Assert.AreEqual("Title", SceneManager.GetActiveScene().name);
                Assert.IsFalse(GameSession.RushESecretCarry, "タイトルへ抜けたら E を次の人へ持ち越さない");
                yield break;
            }
            calibration.DiscardAndExit();
            yield return ScreenTransitionPlayTests.WaitForTransition();
            skin = Object.FindFirstObjectByType<SongSelectSkin>();
            deadline = Time.realtimeSinceStartup + 10;
            while ((skin == null || !skin.IsReady) && Time.realtimeSinceStartup < deadline) { yield return null; skin = Object.FindFirstObjectByType<SongSelectSkin>(); }
            ctl = Object.FindFirstObjectByType<SongSelectController>();
            Assert.AreEqual("RushE", ctl.SongIdAt(ctl.SelectedIndex));
            Assert.IsTrue(ctl.RushESecret.Unlocked); Assert.IsFalse(ctl.RushESecret.Transitioning);
            Assert.AreEqual("E", ctl.CurrentDifficultyName, "判定調整から戻っても E のまま");
            Assert.IsFalse(GameSession.RushESecretCarry, "持ち越しは一度で消す");
            while (ScreenTransition.IsBusy) yield return null;
        }
    }

    // 赤(照準)でジャケットを横切って隣の盤へ移っても、切断には数えたまま、照準をロックせず曲送りも遅らせない。
    [UnityTest]
    public IEnumerator RedSlashIntoTheNextDiscCountsWithoutLockingOrDelayingTheAim()
    {
        if (!File.Exists(Path.Combine(Application.streamingAssetsPath, "Songs", "RushE", "audio.wav"))) Assert.Ignore("文化祭用ローカル音源が必要です。");
        var live = new LiveSabers();
        try
        {
            yield return live.OpenRushE();
            var (_, _, left, right, empty) = live.Layout();
            Vector2 from = right.ScreenRect().center, to = left.ScreenRect().center;
            int rush = live.ctl.SelectedIndex, next = int.Parse(left.name.Substring("SongDisc_".Length));
            yield return live.Drive(.8f, t => empty);
            Assert.Zero(live.aim.Progress01, "どの的にも乗らない所から始める");
            yield return live.Drive(.6f, t => Vector2.Lerp(empty, from, t));
            yield return live.Drive(.2f, t => from);
            Assert.Zero(live.ctl.RushESecret.Hits);
            bool locked = false, hidden = false;
            System.Action watch = () => { locked |= live.aim.NeedsRelease; hidden |= !live.aim.Reticle.gameObject.activeSelf; };
            yield return live.Drive(.35f, t => Vector2.Lerp(from, to, t), null, watch);
            Assert.AreEqual(1, live.ctl.RushESecret.Hits, "横切りは隠し切断に数える");
            float arrived = Time.realtimeSinceStartup;
            while (live.ctl.SelectedIndex == rush && Time.realtimeSinceStartup < arrived + 3) yield return live.Frame(to, null, watch);
            float elapsed = Time.realtimeSinceStartup - arrived;
            Assert.AreEqual(next, live.ctl.SelectedIndex, "左の盤で曲送りする");
            Assert.IsFalse(locked, "切っても照準を灰色にロックしない");
            Assert.IsFalse(hidden, "照準を消さない");
            Assert.Less(elapsed, 1.25f, "切らない曲と同じ約1秒で送る(入場扱いにすると約1.34秒)");
        }
        finally { live.Close(); }
    }

    // 青の剣でジャケットを切っても、赤(照準)のスタートのためには触れない。
    [UnityTest]
    public IEnumerator BlueSlashLeavesTheRedAimCharging()
    {
        if (!File.Exists(Path.Combine(Application.streamingAssetsPath, "Songs", "RushE", "audio.wav"))) Assert.Ignore("文化祭用ローカル音源が必要です。");
        var live = new LiveSabers();
        try
        {
            yield return live.OpenRushE();
            var (centre, radius, _, _, empty) = live.Layout();
            Vector2 above = centre + Vector2.up * radius * 1.3f;
            Vector2 blueFrom = centre + Vector2.left * radius * 1.3f, blueTo = centre + Vector2.right * radius * 1.3f;
            yield return live.Drive(.8f, t => empty);
            yield return live.Drive(.6f, t => Vector2.Lerp(empty, above, t));
            // 真ん中で止まるので、赤自身の動きは切断に数えない(入った所から 1.15 半径に届かない)。
            yield return live.Drive(.3f, t => Vector2.Lerp(above, centre, t));
            yield return live.Drive(.3f, t => centre);
            Assert.Zero(live.ctl.RushESecret.Hits);
            float before = live.aim.Progress01, last = before;
            Assert.Greater(before, 0f, "赤でスタートをためている");
            bool dropped = false, locked = false;
            System.Action watch = () => { dropped |= live.aim.Progress01 + .0001f < last; last = live.aim.Progress01; locked |= live.aim.NeedsRelease; };
            yield return live.Drive(.1f, t => centre, t => blueFrom, watch);
            yield return live.Drive(.6f, t => centre, t => Vector2.Lerp(blueFrom, blueTo, t), watch);
            yield return live.Drive(.1f, t => centre, t => blueTo, watch);
            Assert.AreEqual(1, live.ctl.RushESecret.Hits, "青の横切りも隠し切断に数える");
            Assert.IsFalse(dropped, "青で切っても赤のためを消さない");
            Assert.IsFalse(locked, "青で切っても赤の照準をロックしない");
            Assert.Greater(live.aim.Progress01, before + .3f);
            Assert.Zero(live.aim.ShotCount); Assert.AreEqual("SongSelect", SceneManager.GetActiveScene().name);
        }
        finally { live.Close(); }
    }

    // 赤(照準)でジャケットを切って真ん中で止めても、スタートを約1秒ためないだけで、照準はロックしない。
    [UnityTest]
    public IEnumerator RedSlashEndingOnTheJacketHoldsOffStartForASecondWithoutLocking()
    {
        if (!File.Exists(Path.Combine(Application.streamingAssetsPath, "Songs", "RushE", "audio.wav"))) Assert.Ignore("文化祭用ローカル音源が必要です。");
        var live = new LiveSabers();
        try
        {
            yield return live.OpenRushE();
            var (centre, radius, _, _, empty) = live.Layout();
            // 右上から盤の右へ回り込み、右から左へ切って、真ん中の少し左で止める。
            Vector2 from = centre + Vector2.right * radius * 1.3f, stop = centre + Vector2.left * radius * .25f;
            yield return live.Drive(.8f, t => empty);
            yield return live.Drive(.6f, t => Vector2.Lerp(empty, from, t));
            yield return live.Drive(.2f, t => from);
            Assert.Zero(live.ctl.RushESecret.Hits);
            float cutAt = -1, peak = 0; bool locked = false;
            yield return live.Drive(.2f, t => Vector2.Lerp(from, stop, t), null, () => { if (cutAt < 0 && live.ctl.RushESecret.Hits > 0) cutAt = Time.realtimeSinceStartup; });
            Assert.AreEqual(1, live.ctl.RushESecret.Hits, "真ん中で止める1振りも数える"); Assert.Greater(cutAt, 0f);
            while (Time.realtimeSinceStartup < cutAt + .7f)
                yield return live.Frame(stop, null, () => { peak = Mathf.Max(peak, live.aim.Progress01); locked |= live.aim.NeedsRelease; });
            Assert.Zero(peak, "切った直後はスタートをためない");
            while (Time.realtimeSinceStartup < cutAt + 1.5f) yield return live.Frame(stop, null, () => locked |= live.aim.NeedsRelease);
            Assert.Greater(live.aim.Progress01, 0f, "約1秒たてば、そのままかざしてスタートできる");
            Assert.IsFalse(locked, "照準は灰色にロックしない");
            Assert.Zero(live.aim.ShotCount);
        }
        finally { live.Close(); }
    }

    // 照準(赤)と青を、実際のUDP座標で約30Hzで動かす。RUSH E を中央にし、感度1・位置補正なしで送る。
    sealed class LiveSabers
    {
        static readonly FieldInfo Calibration = typeof(InputPoint).GetField("positionCalibrationEnabled", BindingFlags.Instance | BindingFlags.NonPublic);
        public SongSelectController ctl;
        public SongSelectSkin skin;
        public SongSelectAimPointer aim;
        public InputPoint input;
        readonly UdpClient sender = new UdpClient();
        float sensitivity; bool calibration, changed;

        public IEnumerator OpenRushE()
        {
            yield return SceneManager.LoadSceneAsync("SongSelect", LoadSceneMode.Single);
            ctl = Object.FindFirstObjectByType<SongSelectController>(); skin = Object.FindFirstObjectByType<SongSelectSkin>();
            float deadline = Time.realtimeSinceStartup + 15;
            while (!skin.IsReady && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(skin.IsReady);
            ctl.Select(Enumerable.Range(0, ctl.SongCount).First(i => ctl.SongIdAt(i) == "RushE")); ctl.SetDifficulty(2);
            deadline = Time.realtimeSinceStartup + 5;
            while ((ScreenTransition.IsBusy || !skin.DiscsSettled) && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(skin.DiscsSettled); Assert.IsFalse(ScreenTransition.IsBusy);
            aim = Object.FindFirstObjectByType<SongSelectAimPointer>(); Assert.IsTrue(aim.enabled, "通常のかざす操作も動かしたまま検証する");
            input = InputPoint.Instance;
            sensitivity = input.sensitivity; calibration = (bool)Calibration.GetValue(input); changed = true;
            input.sensitivity = 1; Calibration.SetValue(input, false);
            Assert.IsTrue(input.ReceiverAlive && input.ReceiverAlive2);
            // 放置の案内が出ていると、スタートの代わりに案内を閉じる狙いになる。
            Object.FindFirstObjectByType<SongSelectIdleGuide>()?.RegisterActivity();
            Canvas.ForceUpdateCanvases();
        }

        public void Close()
        {
            if (changed) { input.sensitivity = sensitivity; Calibration.SetValue(input, calibration); }
            if (ctl != null) ctl.StopPreview();
            // 残ったためで終了後にスタートし、次の試験の読み込みと重ならないようにする。
            if (aim != null) aim.BeginArrival();
            sender.Dispose();
        }

        // 中央の盤(ジャケット)、左右の隣の盤(2曲先より大きい方)、どの的にも乗らない右上の点。
        public (Vector2 centre, float radius, SongSelectDiscTarget left, SongSelectDiscTarget right, Vector2 empty) Layout()
        {
            Rect jacket = skin.SelectedDiscScreenRect; Vector2 c = jacket.center;
            var discs = Object.FindObjectsByType<SongSelectDiscTarget>(FindObjectsSortMode.None).Where(d => d.Available && d.RepeatWhileHeld).ToArray();
            var left = discs.Where(d => d.ScreenRect().center.x < c.x).OrderByDescending(d => d.ScreenRect().width).First();
            var right = discs.Where(d => d.ScreenRect().center.x > c.x).OrderByDescending(d => d.ScreenRect().width).First();
            return (c, Mathf.Min(jacket.width, jacket.height) * .5f, left, right, new Vector2(Screen.width * .94f, Screen.height * .9f));
        }

        // seconds かけて赤・青を動かす(t は 0→1)。同じ座標を返せば、その場で構え続ける。blue が null なら青は送らない。
        public IEnumerator Drive(float seconds, System.Func<float, Vector2> red, System.Func<float, Vector2> blue = null, System.Action watch = null)
        {
            int frames = Mathf.Max(1, Mathf.RoundToInt(seconds * 30));
            for (int i = 1; i <= frames; i++)
                yield return Frame(red(i / (float)frames), blue != null ? blue(i / (float)frames) : (Vector2?)null, watch);
        }

        // 赤・青を1回ずつ送り、届いてからカメラと同じ約30Hzの間隔まで進める。watch は毎フレーム呼ぶ。
        public IEnumerator Frame(Vector2 red, Vector2? blue, System.Action watch = null)
        {
            float start = Time.realtimeSinceStartup;
            double previousRed = input.LastReceivedTime, previousBlue = input.LastReceivedTime2;
            Send(input.port, red);
            if (blue.HasValue) Send(input.port2, blue.Value);
            while ((input.LastReceivedTime <= previousRed || (blue.HasValue && input.LastReceivedTime2 <= previousBlue))
                && Time.realtimeSinceStartup < start + 1) { watch?.Invoke(); yield return null; }
            while (Time.realtimeSinceStartup - start < 1f / 30) { watch?.Invoke(); yield return null; }
            watch?.Invoke();
        }

        void Send(int port, Vector2 pixels)
        {
            byte[] bytes = Payload(pixels);
            sender.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, port));
        }
    }
}
