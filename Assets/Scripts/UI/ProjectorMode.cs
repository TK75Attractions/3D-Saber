using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// プロジェクターモード(高コントラスト表示)。
// プロジェクターは黒が灰色に浮くため、暗い背景をさらに暗くしても効かない。代わりに
//   ・ポストプロセス(露出+ / コントラスト+ / 彩度+)で前景を持ち上げる(ここで Volume を生成)
//   ・フォグを薄くして奥のノーツが背景に溶けないようにする(GameStageSkin.FogDensity が参照)
//   ・ノーツ / 判定ゲート / 小節線 / セーバーの線を太く・明るくする(各クラスが DisplaySettings.ProjectorMode を参照)
//   ・紫や深い青など灰色地で輝度差が出ない色を、明るさで見分けられる色へ寄せる(NoteColor)
//   ・UI の補助文字を明るくし、縁取りを付ける
// 数値はこのクラスに集約する。DisplaySettings.ProjectorMode を切り替えたら Apply() を呼び直す。
public static class ProjectorMode
{
    // ---- ポストプロセス ----
    public const float PostExposure = 0.35f;   // EV
    public const float Contrast = 25f;         // %
    public const float Saturation = 18f;       // %

    // ---- ノーツ ----
    public const float NoteScale = 1.12f;      // 正面サイズの倍率(x/y のみ。ロングの z 伸長には触れない)
    public const float NoteEmission = 2.6f;    // NoteVisuals.baseEmissionStrength の下限(通常 1.9)
    public const float NoteRailThickness = 0.06f; // 縁取りレールの太さ(通常 0.034)
    public static readonly Color ArrowBarColor = new Color(0.97f, 0.98f, 1f);          // 矢印は白(通常は黒)
    public static readonly Color ArrowBackingColor = new Color(0.03f, 0.03f, 0.06f, 0.78f); // 下敷きは暗く(通常は明るい)
    public const float ArrowBarWidth = 0.12f;  // 通常 0.09

    // ---- セーバー ----
    public const float BladeWidthScale = 1.6f;
    public const float BladeOutlineScale = 1.9f; // 暗い縁取り線の幅(刃幅比)
    public static readonly Color BladeOutlineColor = new Color(0.02f, 0.02f, 0.05f, 1f);

    // ---- UI ----
    public const float UiOutlineWidth = 0.12f;
    public static readonly Color32 UiOutlineColor = new Color32(0, 0, 0, 210);

    public const string VolumeObjectName = "ProjectorModeVolume";

    public static bool Enabled => DisplaySettings.ProjectorMode;

    // ノーツの色を「明るさで見分けられる」色へ寄せる。
    // バイオレット(フリック)と深い青(左手)は灰色地で輝度差が出ないため置き換え、それ以外はわずかに白へ寄せる。
    public static Color NoteColor(Color c)
    {
        if (Approximately(c, UISkinPalette.NoteFlick)) return new Color(1f, 0.45f, 0.85f);
        if (Approximately(c, UISkinPalette.LogoBlue)) return new Color(0.38f, 0.86f, 1f);
        return Color.Lerp(c, Color.white, 0.08f);
    }

    static bool Approximately(Color a, Color b)
    {
        return Mathf.Abs(a.r - b.r) < 0.01f && Mathf.Abs(a.g - b.g) < 0.01f && Mathf.Abs(a.b - b.b) < 0.01f;
    }

    // ポストプロセスをカメラへ適用/解除する(冪等)。UISkinBootstrap がシーン読込ごとに、
    // ProjectorModeHotkey が切替時に呼ぶ。戻り値は共有 Volume(OFF 時は無効化された既存 Volume か null)。
    public static Volume Apply(Camera cam)
    {
        var volume = FindVolume();
        if (!Enabled)
        {
            if (volume != null) volume.enabled = false;
            if (cam != null) SetPostProcessing(cam, false);
            return volume;
        }
        if (volume == null) volume = CreateVolume();
        volume.enabled = true;
        if (cam != null) SetPostProcessing(cam, true);
        return volume;
    }

    static Volume FindVolume()
    {
        var go = GameObject.Find(VolumeObjectName);
        return go != null ? go.GetComponent<Volume>() : null;
    }

    static Volume CreateVolume()
    {
        var go = new GameObject(VolumeObjectName);
        if (Application.isPlaying) Object.DontDestroyOnLoad(go);
        var volume = go.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 100f; // シーン側の Volume より優先

        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        profile.name = "ProjectorModeProfile";
        profile.hideFlags = HideFlags.HideAndDontSave;
        var ca = profile.Add<ColorAdjustments>(true);
        ca.postExposure.Override(PostExposure);
        ca.contrast.Override(Contrast);
        ca.saturation.Override(Saturation);
        // シーン既定の Bloom はプロジェクターでは霞にしかならないので効かせない
        var bloom = profile.Add<Bloom>(true);
        bloom.intensity.Override(0f);
        volume.profile = profile;
        return volume;
    }

    static void SetPostProcessing(Camera cam, bool on)
    {
        var data = cam.GetUniversalAdditionalCameraData();
        if (data != null) data.renderPostProcessing = on;
    }
}
