using UnityEngine;
using UnityEngine.SceneManagement;

// 本編は UnlockFrameRate が vsync を切り、60 Hz の画面へ約900 fps で描く（2026-10-13 のビルド済みアプリで実測）。
// 画面の破れ・コマ揺れと比べられるよう、台ごとに「画面同期 ON（vsync=1）」を明示選択できるようにする。
// 既定は「ゲームの設定のまま」で、このときは何も変更しない。Scene・ゲームコードには触れない。
public static class PhoneSaberDisplaySyncSettings
{
    public const int GameDefault = 0;
    public const int VSync = 1;

    public static string Key(string station) =>
        "PhoneSaber.DisplaySync." + PhoneSaberStation.Normalize(station) + ".v1";

    public static int Clamp(int mode) => mode == VSync ? VSync : GameDefault;

    public static int Load(string station) => Clamp(PlayerPrefs.GetInt(Key(station), GameDefault));

    public static void Save(string station, int mode)
    {
        PlayerPrefs.SetInt(Key(station), Clamp(mode));
        PlayerPrefs.Save();
    }

    // 書き込む vSyncCount。変更不要なら -1。ゲーム側が後から 0 に戻しても毎フレームの比較で戻す。
    public static int VSyncCountToApply(int mode, int currentVSyncCount) =>
        Clamp(mode) == VSync && currentVSyncCount != 1 ? 1 : -1;
}

// vsync とは別の上限。ゲームは vsync を切って上限なし（targetFrameRate=-1）で描くので、60 Hz の画面でも
// 約1100 fps になり、起動しているだけで Mac が熱くなっていた（2026-10-15 events.log の event=perf、Result 画面）。
// 遅延は vsync の有無で差が出なかった（2026-10-14 実測）ため、未設定の台は「画面の2倍」を既定にする。
// 上限は下げる向きだけに効かせ、ゲームが自分で決めた低い値（メニューの 30 fps など）は上げない。
// 本編（Game）以外のメニュー画面は入力の遅れが効かないので画面と同じ回数（60 Hz なら 60 fps）にする
// （2026-10-16: 上限 120 でもタイトルで CPU 約40%、Mac が熱いまま）。F9 などの計測中は本編と同じ倍率のまま。
public static class PhoneSaberFrameCapSettings
{
    public const int GameDefault = 0;
    public const int FourTimes = 1;
    public const int TwoTimes = 2;

    public static string Key(string station) =>
        "PhoneSaber.FrameCap." + PhoneSaberStation.Normalize(station) + ".v1";

    public static int Clamp(int mode) => mode == FourTimes || mode == TwoTimes ? mode : GameDefault;
    public const int Default = TwoTimes;
    public static int Load(string station) => Clamp(PlayerPrefs.GetInt(Key(station), Default));

    public static void Save(string station, int mode)
    {
        PlayerPrefs.SetInt(Key(station), Clamp(mode));
        PlayerPrefs.Save();
    }

    public static string Label(int mode) => Clamp(mode) == FourTimes ? "画面の4倍" :
        Clamp(mode) == TwoTimes ? "画面の2倍" : "ゲームの設定のまま";

    // 59.94 Hz は 240 / 120 fps（メニューは 60 fps）へ丸める。Hz 不明・範囲外・変更不要・ゲームの値がすでに上限以下なら -1。
    public static int TargetFrameRateToApply(int mode, double refreshHz, int currentTarget, bool menu = false)
    {
        mode = Clamp(mode);
        if (mode == GameDefault || !(refreshHz > 0)) return -1;
        double target = System.Math.Round(refreshHz * (menu ? 1 : mode == FourTimes ? 4 : 2));
        if (!(target >= 1 && target <= int.MaxValue)) return -1;
        return currentTarget > 0 && currentTarget <= (int)target ? -1 : (int)target;
    }

    // 計測中のメニューでは、自分で下げたメニューの上限（menuCap）だけを無いものとして本編の倍率まで上げ直す。
    // 2026-10-29 台B: 上げ直さずに 60 fps のまま F9 で測り、本編（120 fps）の画面条件と一致せず実測が使われなかった。
    // ゲームが自分で決めた低い値（30 fps など）は計測中も上げない。
    public static int CurrentForCap(int currentTarget, int menuCap, bool measuringInMenu) =>
        measuringInMenu && currentTarget > 0 && currentTarget == menuCap ? -1 : currentTarget;
}

public sealed class PhoneSaberDisplaySync : MonoBehaviour
{
    public static int Mode { get; private set; }
    public static int FrameCapMode { get; private set; }
    // 注意の表示確認用(-phonesaberSmokeRefreshHz)。0 なら実際の画面の値を使う。
    public static double RefreshHzOverride;
    // 遅延の計測（F9・追従計測）中は true。メニューでも本編と同じ回数で描く（計測値を本編と揃える）。
    public static bool Measuring;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void CreateAtStartup()
    {
        if (!PhoneSaberP2PBridgeProcess.AutoStartEnabled) return; // テスト中は作らない
        Mode = PhoneSaberDisplaySyncSettings.Load(PhoneSaberStation.Read());
        FrameCapMode = PhoneSaberFrameCapSettings.Load(PhoneSaberStation.Read());
        var go = new GameObject("PhoneSaber Display Sync");
        go.AddComponent<PhoneSaberDisplaySync>().notice = go.AddComponent<PhoneSaberDisplayNotice>();
        DontDestroyOnLoad(go);
    }

    static PhoneSaberDisplaySync instance;
    PhoneSaberDisplayNotice notice;
    double nextNoticeCheck;
    int sceneHandle;
    bool inGameScene;
    // メニュー用に自分で入れた上限。計測の間だけ外す。
    int menuCap = -1;

    void Awake() => instance = this;

    void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    // F9・追従計測の開始と終了。最初のブロックより先に上限を切り替えて、本編と同じ条件で測る
    // （次の Update を待つと、最初のブロックが「測定中に画面設定が変わった」として捨てられる）。
    public static void SetMeasuring(bool measuring)
    {
        Measuring = measuring;
        if (instance != null) instance.ApplyFrameCap();
    }

    // OFF へ戻したときは値を書き戻さない（ゲームが次のシーンで自分の設定を入れ直す）。
    public static void SetMode(int mode)
    {
        mode = PhoneSaberDisplaySyncSettings.Clamp(mode);
        if (mode == Mode) return;
        Mode = mode;
        string station = PhoneSaberStation.Read();
        PhoneSaberDisplaySyncSettings.Save(station, mode);
        PhoneSaberEventLog.Record(station, "SYSTEM", "display-sync",
            mode == PhoneSaberDisplaySyncSettings.VSync ? "mode=vsync" : "mode=game-default");
    }

    // ゲーム既定へ戻すときは上書きを止めるだけ。次のシーンでゲーム自身の設定が入る。
    public static void SetFrameCapMode(int mode)
    {
        mode = PhoneSaberFrameCapSettings.Clamp(mode);
        if (mode == FrameCapMode) return;
        FrameCapMode = mode;
        string station = PhoneSaberStation.Read();
        PhoneSaberFrameCapSettings.Save(station, mode);
        PhoneSaberEventLog.Record(station, "SYSTEM", "frame-cap", "mode=" + mode);
    }

    // UnlockFrameRate.Awake より後に効かせるため Update で見る（int の比較1回）。
    void Update()
    {
        int value = PhoneSaberDisplaySyncSettings.VSyncCountToApply(Mode, QualitySettings.vSyncCount);
        if (value >= 0) QualitySettings.vSyncCount = value;
        ApplyFrameCap();
        // 画面の更新が遅い（低電力モードなど）ときの知らせ。確認は秒1回、描画は必要なときだけ有効にする。
        double now = Time.realtimeSinceStartupAsDouble;
        if (notice == null || now < nextNoticeCheck) return;
        nextNoticeCheck = now + 1;
        string sceneName = SceneManager.GetActiveScene().name;
        notice.Text = PhoneSaberDisplayNotice.Join(PhoneSaberDisplayNotice.TextFor(
            PhoneSaberStatsDisplay.DisplayWarning(RefreshHzOverride > 0
                ? RefreshHzOverride : Screen.currentResolution.refreshRateRatio.value),
            sceneName), sceneName == PhoneSaberDisplayNotice.GameSceneName ? "" : PhoneSaberDisplayNotice.PhoneWarning);
        notice.enabled = notice.Text.Length > 0;
    }

    void ApplyFrameCap()
    {
        if (FrameCapMode == PhoneSaberFrameCapSettings.GameDefault) return;
        // シーン名は切り替わったときだけ読む（毎フレームの文字列確保を避ける）。
        Scene scene = SceneManager.GetActiveScene();
        if (scene.handle != sceneHandle)
        {
            sceneHandle = scene.handle;
            inGameScene = scene.name == PhoneSaberDisplayNotice.GameSceneName;
        }
        bool menu = !inGameScene && !Measuring;
        int current = PhoneSaberFrameCapSettings.CurrentForCap(Application.targetFrameRate, menuCap, !inGameScene && Measuring);
        int target = PhoneSaberFrameCapSettings.TargetFrameRateToApply(FrameCapMode,
            Screen.currentResolution.refreshRateRatio.value, current, menu);
        if (target < 0) return;
        Application.targetFrameRate = target;
        menuCap = menu ? target : -1;
    }
}

// F8 を開かなくても気づけるよう、メニュー画面の上端に出す（2026-10-13: 低電力モードのまま起動して
// 「アプリにすると重い」となることが続いた）。本編（Game）では出さない（プレイ画面を隠さない）。
public sealed class PhoneSaberDisplayNotice : MonoBehaviour
{
    public const string GameSceneName = "Game";
    public string Text = "";
    GUIStyle style;

    // スマホの発熱・テザリング親機など（PhoneSaberJudgeLatency が数秒ごとに更新）。メニュー画面だけに出す。
    public static string PhoneWarning = "";

    public static string Join(string a, string b) =>
        string.IsNullOrEmpty(a) ? b ?? "" : string.IsNullOrEmpty(b) ? a : a + "\n" + b;

    // 2026-10-17 実測: iPhone がテザリングの親機（172.20.10.1）のまま thermal=serious になり、ずっと 30 fps だった。
    public static string PhoneWarningFor(string sender, string thermal, double fpsMode) =>
        PhoneWarningFor(sender, thermal, fpsMode, false);

    // viaUsb: USB のインターネット共有（2026-10-29 台B）。送信元は同じ 172.20.10.1 だが Wi-Fi の電波で親機をしないので、
    // 親機の注意は出さない。熱と 30fps は thermal・fpsMode で今までどおり知らせる。
    public static string PhoneWarningFor(string sender, string thermal, double fpsMode, bool viaUsb)
    {
        string text = "";
        if (thermal == "serious" || thermal == "critical")
            text = Join(text, $"スマホが熱い（{thermal}）: カメラが30fpsに落ちて遅れが増えます。ケースを外す・充電を外す・風を当てて冷ましてください。");
        else if (fpsMode > 0 && fpsMode < 50)
            text = Join(text, "スマホのカメラが30fpsです（60fpsより遅れが約15ms増）。発熱か設定を確認してください。");
        if (sender == PhoneSaberUsbTether.TetheringHost && !viaUsb)
            text = Join(text, "スマホがテザリングの親機です。Wi-Fi で親機をすると発熱します。Wi-Fiルーター・別の端末を親機にするか、USBケーブルでつないでください。");
        return text;
    }

    public static string TextFor(string displayWarning, string sceneName) => TextFor(displayWarning, sceneName, Application.platform);

    // 直す場所は OS ごとに違う(Windows の台にも Mac の手順を出していた。2026-10-21)。
    public static string TextFor(string displayWarning, string sceneName, RuntimePlatform platform) =>
        string.IsNullOrEmpty(displayWarning) || sceneName == GameSceneName ? "" : displayWarning + FixHint(platform);

    static string FixHint(RuntimePlatform platform) =>
        platform == RuntimePlatform.OSXPlayer || platform == RuntimePlatform.OSXEditor
            ? "\n（Mac: システム設定 > バッテリー > 低電力モード を「しない」に）"
            : platform == RuntimePlatform.WindowsPlayer || platform == RuntimePlatform.WindowsEditor
                ? "\n（Windows: 設定 > システム > ディスプレイ > ディスプレイの詳細設定 で リフレッシュレートを 60Hz に）"
                : "";

    void Awake() => enabled = false;

    void OnGUI()
    {
        if (Text.Length == 0) return;
        if (style == null)
        {
            style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
            style.normal.textColor = Color.yellow;
        }
        style.fontSize = Mathf.Max(14, Screen.height / 40);
        int lines = 1;
        foreach (char c in Text) if (c == '\n') lines++;
        float height = style.fontSize * (1.2f + 1.3f * lines);
        GUI.Box(new Rect(Screen.width * 0.1f, 8, Screen.width * 0.8f, height), Text, style);
    }
}

// 2026-10-29 台B: iPhone のインターネット共有を USB でつなぐと、Wi-Fi の親機のときと同じく送信元が 172.20.10.1 になる。
// この PC の 172.20.10.0/28 の番地が Apple Mobile Device Ethernet（Windows の iPhone USB ドライバー）にあれば USB とみなす。
// 一覧の取得は数十 ms かかることがあるので、送信元が 172.20.10.1 のときだけ裏のスレッドで 10 秒ごとに調べる。
// Mac の「iPhone USB」は Mono の一覧から Wi-Fi と見分けられないので、Mac では今までどおり親機の注意を出す。
public static class PhoneSaberUsbTether
{
    public const string TetheringHost = "172.20.10.1";
    public const string WindowsAdapter = "Apple Mobile Device Ethernet";
    public const double RefreshSeconds = 10;

    const int Unknown = 0, Usb = 1, NotUsb = 2;
    static int state = Unknown;
    static int scanning;
    static string localAddress = "";
    // ここから下は main thread だけが触る。
    static double nextScanAt = double.NegativeInfinity;
    static int loggedState = Unknown;

    // iPhone の親機が配る 172.20.10.0/28。
    public static bool InTetheringSubnet(string address)
    {
        if (string.IsNullOrEmpty(address)) return false;
        string[] parts = address.Split('.');
        if (parts.Length != 4) return false;
        var values = new int[4];
        for (int i = 0; i < 4; i++)
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out values[i]) || values[i] > 255) return false;
        return values[0] == 172 && values[1] == 20 && values[2] == 10 && values[3] <= 15;
    }

    public static bool IsUsbTetherInterface(string description, System.Collections.Generic.IEnumerable<string> addresses)
    {
        if (description == null || addresses == null ||
            !description.StartsWith(WindowsAdapter, System.StringComparison.OrdinalIgnoreCase)) return false;
        foreach (string address in addresses)
            if (InTetheringSubnet(address)) return true;
        return false;
    }

    // main thread から（PhoneSaberJudgeLatency が 2 秒ごと）。調べ終わる前は USB 扱いにして、注意を一瞬だけ出すことをしない。
    public static bool ViaUsb(string sender, double now)
    {
        if (sender != TetheringHost) return false;
        if (now >= nextScanAt)
        {
            nextScanAt = now + RefreshSeconds;
            ScanInBackground();
        }
        int current = System.Threading.Volatile.Read(ref state);
        if (current != Unknown && current != loggedState)
        {
            loggedState = current;
            PhoneSaberEventLog.Record(PhoneSaberStation.Read(), "SYSTEM", "usb-tether",
                current == Usb ? "usb=1 local=" + System.Threading.Volatile.Read(ref localAddress) : "usb=0");
        }
        return current != NotUsb;
    }

    static void ScanInBackground()
    {
        if (System.Threading.Interlocked.CompareExchange(ref scanning, 1, 0) != 0) return;
        try
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string found = Scan();
                    System.Threading.Volatile.Write(ref localAddress, found ?? "");
                    System.Threading.Volatile.Write(ref state, found != null ? Usb : NotUsb);
                }
                catch (System.Exception) { System.Threading.Volatile.Write(ref state, NotUsb); }
                finally { System.Threading.Volatile.Write(ref scanning, 0); }
            });
        }
        catch (System.Exception) { System.Threading.Volatile.Write(ref scanning, 0); }
    }

    // USB 側のこの PC の番地（無ければ null）。動いている Apple Mobile Device Ethernet だけを見る。
    static string Scan()
    {
        foreach (var network in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (network.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                var addresses = new System.Collections.Generic.List<string>();
                foreach (var unicast in network.GetIPProperties().UnicastAddresses)
                    if (unicast.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        addresses.Add(unicast.Address.ToString());
                if (!IsUsbTetherInterface(network.Description, addresses)) continue;
                foreach (string address in addresses)
                    if (InTetheringSubnet(address)) return address;
            }
            catch (System.Exception) { }
        }
        return null;
    }
}
