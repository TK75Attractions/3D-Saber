using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;

// InputPoint の座標変換純関数の検証。
// 「棒2だけ direct モードでピクセル→正規化変換が抜けていて画面隅に張り付く」バグの回帰防止。
// 現在は棒1・棒2とも CanonicalizePoint / Normalized01 を通るため、対称性は構造的に保証される。
public class InputPointConversionTests
{
    const float W = 1920f;
    const float H = 1080f;

    // ---- direct world mapping モード(本番構成: EnsureInputPoint が有効化) ----

    [Test]
    public void Canonicalize_Direct_PixelInput_IsNormalized()
    {
        // これが落ちていたのが棒2バグ。ピクセル(960,540)=画面中央 → 正規化(0,0)
        Vector2 center = InputPoint.CanonicalizePoint(960f, 540f, W, H, true);
        Assert.AreEqual(0f, center.x, 1e-4f);
        Assert.AreEqual(0f, center.y, 1e-4f);

        Vector2 corner = InputPoint.CanonicalizePoint(1920f, 1080f, W, H, true);
        Assert.AreEqual(1f, corner.x, 1e-4f);
        Assert.AreEqual(1f, corner.y, 1e-4f);

        Vector2 origin = InputPoint.CanonicalizePoint(0f, 0f, W, H, true);
        Assert.AreEqual(0f, origin.x, 1e-4f, "(0,0)は正規化扱い(|v|<=1.5)でそのまま");
    }

    [Test]
    public void Canonicalize_Direct_NormalizedInput_PassesThrough()
    {
        Vector2 v = InputPoint.CanonicalizePoint(0.5f, -0.75f, W, H, true);
        Assert.AreEqual(0.5f, v.x, 1e-4f);
        Assert.AreEqual(-0.75f, v.y, 1e-4f);
    }

    // ---- legacy モード(boardRect 経由の旧チェーン) ----

    [Test]
    public void Canonicalize_Legacy_NormalizedInput_BecomesPixels()
    {
        Vector2 v = InputPoint.CanonicalizePoint(0f, 0f, W, H, false);
        Assert.AreEqual(960f, v.x, 1e-2f);
        Assert.AreEqual(540f, v.y, 1e-2f);

        Vector2 max = InputPoint.CanonicalizePoint(1f, 1f, W, H, false);
        Assert.AreEqual(1920f, max.x, 1e-2f);
        Assert.AreEqual(1080f, max.y, 1e-2f);
    }

    [Test]
    public void Canonicalize_Legacy_PixelInput_PassesThrough()
    {
        Vector2 v = InputPoint.CanonicalizePoint(462f, 891f, W, H, false);
        Assert.AreEqual(462f, v.x, 1e-3f);
        Assert.AreEqual(891f, v.y, 1e-3f);
    }

    // ---- 判別は「点単位」(片軸だけ小さくてもピクセル点として扱う) ----

    [Test]
    public void Canonicalize_MixedMagnitudePoint_IsTreatedAsPixels()
    {
        // x=0.5 だが y=891 → この点はピクセル座標とみなす(棒1の従来仕様を踏襲)
        Vector2 v = InputPoint.CanonicalizePoint(0.5f, 891f, W, H, true);
        Assert.AreEqual((0.5f / W) * 2f - 1f, v.x, 1e-4f);
        Assert.AreEqual((891f / H) * 2f - 1f, v.y, 1e-4f);
    }

    // ---- NormalizedPosition(0..1) ----

    [Test]
    public void Normalized01_BothInputKinds()
    {
        Vector2 fromNorm = InputPoint.Normalized01(-1f, 1f, W, H);
        Assert.AreEqual(0f, fromNorm.x, 1e-4f);
        Assert.AreEqual(1f, fromNorm.y, 1e-4f);

        Vector2 fromPixel = InputPoint.Normalized01(960f, 270f, W, H);
        Assert.AreEqual(0.5f, fromPixel.x, 1e-4f);
        Assert.AreEqual(0.25f, fromPixel.y, 1e-4f);
    }

    // ---- 感度(中央基準で移動量を増幅。既定2倍) ----

    [Test]
    public void Sensitivity_Direct_DoublesAroundCenter()
    {
        // 中心0基準: (0.25, -0.3) → 2倍 → (0.5, -0.6)
        Vector2 v = InputPoint.ApplySensitivity(new Vector2(0.25f, -0.3f), 2f, true, W, H);
        Assert.AreEqual(0.5f, v.x, 1e-4f);
        Assert.AreEqual(-0.6f, v.y, 1e-4f);

        // 中央は動かない
        Vector2 c = InputPoint.ApplySensitivity(Vector2.zero, 2f, true, W, H);
        Assert.AreEqual(0f, c.x, 1e-4f);
        Assert.AreEqual(0f, c.y, 1e-4f);
    }

    [Test]
    public void Sensitivity_Direct_ClampsAtEdge()
    {
        // 2倍で ±1 を超える分は画面端でクランプ(画面外へ飛ばさない)
        Vector2 v = InputPoint.ApplySensitivity(new Vector2(0.8f, -0.9f), 2f, true, W, H);
        Assert.AreEqual(1f, v.x, 1e-4f);
        Assert.AreEqual(-1f, v.y, 1e-4f);
    }

    [Test]
    public void Sensitivity_One_IsIdentity()
    {
        Vector2 v = InputPoint.ApplySensitivity(new Vector2(0.37f, -0.62f), 1f, true, W, H);
        Assert.AreEqual(0.37f, v.x, 1e-4f);
        Assert.AreEqual(-0.62f, v.y, 1e-4f);

        Vector2 p = InputPoint.ApplySensitivity(new Vector2(462f, 891f), 1f, false, W, H);
        Assert.AreEqual(462f, p.x, 1e-3f);
        Assert.AreEqual(891f, p.y, 1e-3f);
    }

    [Test]
    public void Sensitivity_Legacy_ScalesAroundPixelCenter()
    {
        // legacy はピクセル中心 (960, 540) 基準。(1200, 270) → (960+240*2, 540-270*2) = (1440, 0)
        Vector2 v = InputPoint.ApplySensitivity(new Vector2(1200f, 270f), 2f, false, W, H);
        Assert.AreEqual(1440f, v.x, 1e-2f);
        Assert.AreEqual(0f, v.y, 1e-2f);

        // 画面外はクランプ
        Vector2 e = InputPoint.ApplySensitivity(new Vector2(1800f, 1000f), 2f, false, W, H);
        Assert.AreEqual(1920f, e.x, 1e-2f);
        Assert.AreEqual(1080f, e.y, 1e-2f);
    }

    [Test]
    public void Sensitivity01_ScalesAroundHalf()
    {
        // NormalizedPosition(0..1)は 0.5 中心。(0.6, 0.25) → (0.7, 0.0)
        Vector2 v = InputPoint.ApplySensitivity01(new Vector2(0.6f, 0.25f), 2f);
        Assert.AreEqual(0.7f, v.x, 1e-4f);
        Assert.AreEqual(0f, v.y, 1e-4f);

        // クランプ確認
        Vector2 e = InputPoint.ApplySensitivity01(new Vector2(0.9f, 0.05f), 2f);
        Assert.AreEqual(1f, e.x, 1e-4f);
        Assert.AreEqual(0f, e.y, 1e-4f);
    }

    [Test]
    public void Sensitivity_StickEndpoints_TranslateWithMidKeepingLength()
    {
        // 端点は「中点の移動量」で平行移動する仕様 → 棒の長さが変わらないことを検証。
        // mid=(0.3,0), 棒は水平で長さ0.4 (endA=0.1, endB=0.5)
        Vector2 mid = new Vector2(0.3f, 0f);
        Vector2 endA = new Vector2(0.1f, 0f);
        Vector2 endB = new Vector2(0.5f, 0f);

        Vector2 sensMid = InputPoint.ApplySensitivity(mid, 2f, true, W, H);
        Vector2 delta = sensMid - mid;
        Vector2 movedA = endA + delta;
        Vector2 movedB = endB + delta;

        // 中点は2倍の位置へ
        Assert.AreEqual(0.6f, sensMid.x, 1e-4f);
        // 平行移動後も棒長は 0.4 のまま(端点ごとにスケールすると 0.8 になってしまう)
        Assert.AreEqual(0.4f, Vector2.Distance(movedA, movedB), 1e-4f);
        // 移動後の端点の中点 = 感度適用後の中点
        Vector2 movedMid = (movedA + movedB) * 0.5f;
        Assert.AreEqual(sensMid.x, movedMid.x, 1e-4f);
        Assert.AreEqual(sensMid.y, movedMid.y, 1e-4f);
    }
}

// 点単体の自動判別とは別に、実際の4値パケット内で単位が揃うことを検証する。
// 受信機は起動せず、実 parser の結果を受信バッファ相当へ入れて main thread の変換だけを呼ぶ。
public class InputPointConversionTests_Packets
{
    const float W = 1920f, H = 1080f;
    const float Epsilon = 1e-4f;
    static readonly Vector2 Scale = new Vector2(5.5f, 3f);
    const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    GameObject owner;
    InputPoint input;
    InputPoint savedInstance;
    bool savedAutoStart, savedIgnoreZones;

    [SetUp]
    public void CreateInactiveInputWithoutNetworkOrSavedSettings()
    {
        savedInstance = InputPoint.Instance;
        savedAutoStart = PhoneSaberP2PBridgeProcess.AutoStartEnabled;
        savedIgnoreZones = PhoneSaberIgnoreZones.Enabled;
        PhoneSaberP2PBridgeProcess.AutoStartEnabled = false;
        // IgnoreZones.Load の PlayerPrefs 読み出しも避ける。保存済み設定は書き換えない。
        PhoneSaberIgnoreZones.Enabled = false;
        owner = new GameObject("パケット単位の座標変換テスト");
        owner.SetActive(false);
        input = owner.AddComponent<InputPoint>();
        input.camWidth = W;
        input.camHeight = H;
        input.useDirectWorldMapping = true;
        input.worldScale = Scale;
        input.worldOffset = Vector2.zero;
        input.sensitivity = 1f;
        input.useImuFallback = false;
        Assert.IsFalse(input.PositionCalibrationEnabled, "ネットワークを起動せず位置補正設定を読み込まない");
        AssertNetworkWasNotStarted();
    }

    [TearDown]
    public void DestroyInputAndRestoreStaticSettings()
    {
        try
        {
            if (input != null) AssertNetworkWasNotStarted();
        }
        finally
        {
            try
            {
                if (owner != null) Object.DestroyImmediate(owner);
            }
            finally
            {
                PhoneSaberP2PBridgeProcess.AutoStartEnabled = savedAutoStart;
                PhoneSaberIgnoreZones.Enabled = savedIgnoreZones;
                typeof(InputPoint).GetProperty("Instance").SetMethod
                    .Invoke(null, new object[] { savedInstance });
                owner = null;
                input = null;
            }
        }
    }

    [TestCase(false, "0,0,1919,1079", 0f, 0f, 1919f, 1079f)]
    [TestCase(true, "1919,1079,0,0", 1919f, 1079f, 0f, 0f)]
    public void ApplyLatestInput_PixelCorner_UsesPixelsForWholePacket(
        bool blue, string payload, float ax, float ay, float bx, float by)
    {
        ApplyPacket(blue, payload);
        AssertPublishedStick(blue, new Vector2(ax, ay), new Vector2(bx, by), false);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ApplyLatestInput_NormalizedStick_KeepsNormalizedCoordinates(bool blue)
    {
        ApplyPacket(blue, "-.5,0,.5,0");
        AssertPublishedStick(blue, new Vector2(-.5f, 0f), new Vector2(.5f, 0f), true);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ApplyLatestInput_NonCornerPixelStick_KeepsPixelCoordinates(bool blue)
    {
        ApplyPacket(blue, "100,200,300,400");
        AssertPublishedStick(blue, new Vector2(100f, 200f), new Vector2(300f, 400f), false);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ApplyLatestInput_PixelEdge_KeepsNormalizedEndpointsAndWorldEndpointsConsistent(bool blue)
    {
        // 片軸だけ小さい点は CanonicalizePoint ではピクセルだが、NormalizeAxis は別判定になる。
        ApplyPacket(blue, "0,200,300,0");
        AssertPublishedStick(blue, new Vector2(0f, 200f), new Vector2(300f, 0f), false);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ApplyLatestInput_PixelStickWithSmallMidpoint_UsesPixelsForMidpointToo(bool blue)
    {
        // 4値の一部が 1.5 を超える一方、中点は (1,1)。中点だけの再判別を検出する。
        ApplyPacket(blue, "0,0,2,2");
        AssertPublishedStick(blue, Vector2.zero, new Vector2(2f, 2f), false);
    }

    [TestCase(false, "0,0", 0f, 0f, true)]
    [TestCase(true, "0,0", 0f, 0f, true)]
    [TestCase(false, ".5,-.75", .5f, -.75f, true)]
    [TestCase(true, ".5,-.75", .5f, -.75f, true)]
    [TestCase(false, "100,200", 100f, 200f, false)]
    [TestCase(true, "100,200", 100f, 200f, false)]
    public void ApplyLatestInput_TwoValuePacketAfterPixelStick_KeepsPointInterpretation(
        bool blue, string payload, float x, float y, bool normalized)
    {
        // 直前の4値の単位判定を2値へ持ち越さず、端点有効フラグも解除する。
        ApplyPacket(blue, "100,200,300,400");
        Assert.IsTrue(blue ? input.HasValidStickEndpoints2 : input.HasValidStickEndpoints);
        ApplyPacket(blue, payload);
        Vector2 raw = new Vector2(x, y);
        Vector2 expected = normalized ? raw : PixelToNormalized(raw);
        Assert.IsFalse(blue ? input.HasValidStickEndpoints2 : input.HasValidStickEndpoints);
        AssertVector(raw, blue ? input.LastRaw2 : input.LastRaw, "2値の生座標");
        AssertVector(ToWorld(expected), blue ? input.LocalPosition2 : input.LocalPosition, "2値のワールド中点");
        AssertVector(To01(expected), blue ? input.NormalizedPosition2 : input.NormalizedPosition, "2値の0..1中点");
    }

    void ApplyPacket(bool blue, string payload)
    {
        var parser = new PhoneSaberPacketParser();
        Assert.IsTrue(parser.TryParse(Encoding.ASCII.GetBytes(payload), out bool stick,
            out float a, out float b, out float c, out float d, out double? sentEpoch));
        Assert.IsTrue(PhoneSaberPacketParser.IsUsableCoordinates(stick, a, b, c, d));
        // ReceiveData と同じ「両端と算術中点」の受信バッファを作る。socket・受信 thread は使わない。
        string pointSuffix = blue ? "2" : "";
        string endPrefix = blue ? "2" : "1";
        if (stick)
        {
            SetField("rawX" + endPrefix + "a", a);
            SetField("rawY" + endPrefix + "a", b);
            SetField("rawX" + endPrefix + "b", c);
            SetField("rawY" + endPrefix + "b", d);
        }
        SetField("rawX" + pointSuffix, stick ? (a + c) * .5f : a);
        SetField("rawY" + pointSuffix, stick ? (b + d) * .5f : b);
        SetField("hasStickData" + pointSuffix, stick);
        SetField("rawReceiveTimestampTicks" + pointSuffix, SwingMonotonicClock.Timestamp);
        SetField("rawSender" + pointSuffix, "192.0.2.1");
        SetField("hasNewData" + pointSuffix, true);
        typeof(InputPoint).GetMethod("ApplyLatestInput", PrivateInstance)
            .Invoke(input, new object[] { false });
    }

    void AssertPublishedStick(bool blue, Vector2 rawA, Vector2 rawB, bool normalized)
    {
        // 期待値は既存の自動判別関数を呼ばず、パケットの既知の単位から計算する。
        Vector2 a = normalized ? rawA : PixelToNormalized(rawA);
        Vector2 b = normalized ? rawB : PixelToNormalized(rawB);
        Vector2 rawMid = (rawA + rawB) * .5f;
        Vector2 mid = (a + b) * .5f;
        Vector2 actualA = blue ? input.LocalStickRawA2 : input.LocalStickRawA;
        Vector2 actualB = blue ? input.LocalStickRawB2 : input.LocalStickRawB;
        Vector2 actualMid = blue ? input.LocalPosition2 : input.LocalPosition;
        Assert.IsTrue(blue ? input.HasValidStickEndpoints2 : input.HasValidStickEndpoints);
        AssertVector(rawMid, blue ? input.LastRaw2 : input.LastRaw, "変換前の中点");
        AssertVector(ToWorld(a), actualA, "ワールド端点A");
        AssertVector(ToWorld(b), actualB, "ワールド端点B");
        AssertVector(ToWorld(mid), actualMid, "ワールド中点");
        AssertVector(actualMid, (actualA + actualB) * .5f, "端点の平均と中点の一致");
        AssertVector(To01(a), blue ? input.LastRawEndsA2 : input.LastRawEndsA, "無視範囲用の端点A");
        AssertVector(To01(b), blue ? input.LastRawEndsB2 : input.LastRawEndsB, "無視範囲用の端点B");
        AssertVector(To01(mid), blue ? input.NormalizedPosition2 : input.NormalizedPosition, "0..1中点");
        AssertVector(a, blue ? input.LocalStickA2 : input.LocalStickA, "-1..1端点A");
        AssertVector(b, blue ? input.LocalStickB2 : input.LocalStickB, "-1..1端点B");
        float length = Vector2.Distance(a, b);
        Assert.AreEqual(length, blue ? input.LocalStickLength2 : input.LocalStickLength, Epsilon, "棒長");
        Assert.AreEqual(length / Mathf.Sqrt(8f), blue ? input.LocalStickLengthNormalized2 : input.LocalStickLengthNormalized,
            Epsilon, "正規化した棒長");
        float angle = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;
        Assert.AreEqual(angle, blue ? input.LocalAngleDeg2 : input.LocalAngleDeg, Epsilon, "棒の角度");
    }

    void AssertNetworkWasNotStarted()
    {
        Assert.IsFalse(owner.activeInHierarchy);
        Assert.AreSame(savedInstance, InputPoint.Instance, "既存の singleton を使わない");
        Assert.IsTrue((bool)Field("networkShutdown"));
        foreach (string name in new[] { "udpClient1", "udpClient2", "receiveThread1", "receiveThread2",
            "receiverStopSignal1", "receiverStopSignal2", "bonjourPublisher", "discoveryResponder", "p2pBridge" })
            Assert.IsNull(Field(name), name + " を生成しない");
    }

    object Field(string name) => typeof(InputPoint).GetField(name, PrivateInstance).GetValue(input);
    void SetField(string name, object value) => typeof(InputPoint).GetField(name, PrivateInstance).SetValue(input, value);
    static Vector2 PixelToNormalized(Vector2 p) => new Vector2(p.x / W * 2f - 1f, p.y / H * 2f - 1f);
    static Vector2 To01(Vector2 p) => (p + Vector2.one) * .5f;
    static Vector2 ToWorld(Vector2 p) => Vector2.Scale(p, Scale);
    static void AssertVector(Vector2 expected, Vector2 actual, string label)
    {
        Assert.AreEqual(expected.x, actual.x, Epsilon, label + " x");
        Assert.AreEqual(expected.y, actual.y, Epsilon, label + " y");
    }
}
