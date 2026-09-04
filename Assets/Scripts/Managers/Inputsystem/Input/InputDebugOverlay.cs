using UnityEngine;
using UnityEngine.InputSystem;

// 入力デバッグ表示(F3 で表示/非表示。既定は非表示なので見た目には影響しない)。
// InputPoint が Start で自分に付けるので、受信機と同じ寿命で全シーンに付いて回る。
// 「ポインタが画面端まで届かない」等の切り分け用に、送信側の生値(感度・写像を掛ける前)、
// 正規化値、ワールド座標、受信レート、SaberInputBridge の実位置を画面左上に出す。
//   生値の |x| が端で 0.5 未満にしか行かない → 送信側(トラッカー)の可動域/中心ずれ
//   生値は ±1 まで行くのに bridge の位置が端に届かない → 受信側(変換)の問題
public class InputDebugOverlay : MonoBehaviour
{
    public Key toggleKey = Key.F3;
    public bool visible = false;

    private InputPoint ip;
    private SaberInputBridge bridge;
    private double lastSeen1 = double.NaN;
    private double lastSeen2 = double.NaN;
    private int count1, count2;
    private float rateTimer;
    private float hz1, hz2;
    private float bridgeSearchTimer;
    private GUIStyle style;

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb[toggleKey].wasPressedThisFrame) visible = !visible;
        if (!visible) return;

        if (ip == null) ip = GetComponent<InputPoint>();
        if (ip == null) ip = InputPoint.Instance;
        if (ip == null) return;

        // 受信レート: LastReceivedTime が変わったフレームを1秒ごとに数える
        if (ip.LastReceivedTime != lastSeen1) { lastSeen1 = ip.LastReceivedTime; count1++; }
        if (ip.LastReceivedTime2 != lastSeen2) { lastSeen2 = ip.LastReceivedTime2; count2++; }
        rateTimer += Time.unscaledDeltaTime;
        if (rateTimer >= 1f)
        {
            hz1 = count1 / rateTimer;
            hz2 = count2 / rateTimer;
            count1 = count2 = 0;
            rateTimer = 0f;
        }

        // シーン遷移で Bridge が入れ替わるので、たまに探し直す
        bridgeSearchTimer -= Time.unscaledDeltaTime;
        if (bridge == null || bridgeSearchTimer <= 0f)
        {
            bridge = Object.FindFirstObjectByType<SaberInputBridge>();
            bridgeSearchTimer = 1f;
        }
    }

    void OnGUI()
    {
        if (!visible || ip == null) return;
        if (style == null)
        {
            style = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.UpperLeft,
                fontSize = 14,
                richText = false,
            };
            style.normal.textColor = Color.white;
        }

        string text = BuildText(ip, bridge, hz1, hz2);
        GUI.Box(new Rect(10f, 10f, 520f, 190f), text, style);
    }

    // 表示文字列の組み立て(純関数。テストから直接叩ける)。
    public static string BuildText(InputPoint ip, SaberInputBridge bridge, float hz1, float hz2)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[Input Debug]  F3: hide   sensitivity={ip.sensitivity:F2}  direct={ip.useDirectWorldMapping}");
        sb.AppendLine($"stick1 raw=({ip.LastRaw.x:F3}, {ip.LastRaw.y:F3})  norm01=({ip.NormalizedPosition.x:F3}, {ip.NormalizedPosition.y:F3})");
        sb.AppendLine($"       world=({ip.LocalPosition.x:F2}, {ip.LocalPosition.y:F2})  {hz1:F0} Hz  active={ip.IsRecentlyActive(1.0)}");
        sb.AppendLine($"stick2 raw=({ip.LastRaw2.x:F3}, {ip.LastRaw2.y:F3})  world=({ip.LocalPosition2.x:F2}, {ip.LocalPosition2.y:F2})  {hz2:F0} Hz  active={ip.IsRecentlyActive2(1.0)}");
        if (bridge != null)
        {
            Vector3 p = bridge.transform.position;
            sb.AppendLine($"bridge pos=({p.x:F2}, {p.y:F2})  remap={bridge.remapToCameraView}  mouse={bridge.UsingMouseFallback}  blade={bridge.HasBlade}");
            if (bridge.HasBlade)
            {
                sb.AppendLine($"       endA=({bridge.WorldEndA.x:F2}, {bridge.WorldEndA.y:F2})  endB=({bridge.WorldEndB.x:F2}, {bridge.WorldEndB.y:F2})");
            }
        }
        else
        {
            sb.AppendLine("bridge: (none in scene)");
        }
        sb.Append("hint: raw |x| stays < 0.5 at the edge -> sender range; raw hits 1.0 but bridge does not -> receiver");
        return sb.ToString();
    }
}
