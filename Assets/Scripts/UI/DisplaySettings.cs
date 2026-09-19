using UnityEngine;

// 表示に関するプレイヤー設定。PlayerPrefs で永続化し、実行中はキャッシュを使う。
// プロジェクターモード = 白っぽく投影される環境向けの高コントラスト表示。
// このゲームはプロジェクターに映して遊ぶ前提なので既定 ON(F4 / 曲選択画面のボタンで切替)。
public static class DisplaySettings
{
    private const string ProjectorModeKey = "displayProjectorMode";
    private static bool? projectorModeCache;
    private const string ReducedEffectsKey = "displayReducedEffects";
    private static bool? reducedEffectsCache;
    public static event System.Action<bool> OnReducedEffectsChanged;
    public static bool ReducedEffects
    {
        get { if (!reducedEffectsCache.HasValue) reducedEffectsCache = PlayerPrefs.GetInt(ReducedEffectsKey, 0) != 0; return reducedEffectsCache.Value; }
        set {
            if (ReducedEffects == value) return;
            reducedEffectsCache = value;
            PlayerPrefs.SetInt(ReducedEffectsKey, value ? 1 : 0); PlayerPrefs.Save();
            OnReducedEffectsChanged?.Invoke(value);
        }
    }
    public static float AccentScale => ReducedEffects ? .3f : 1f;
    // テスト・撮影は実ユーザーの設定を保存しない。
    public static void SetReducedEffectsForTest(bool value) { reducedEffectsCache = value; }
    public static void ResetReducedEffectsCacheForTest() { reducedEffectsCache = null; }

    public static event System.Action<bool> OnProjectorModeChanged;

    public static bool ProjectorMode
    {
        get
        {
            if (projectorModeCache == null)
            {
                projectorModeCache = PlayerPrefs.GetInt(ProjectorModeKey, 1) != 0;
            }
            return projectorModeCache.Value;
        }
        set
        {
            if (projectorModeCache == value) return;
            projectorModeCache = value;
            PlayerPrefs.SetInt(ProjectorModeKey, value ? 1 : 0);
            PlayerPrefs.Save();
            OnProjectorModeChanged?.Invoke(value);
        }
    }

    // テスト用: 永続化せずにモードを差し替える(他のテストへ影響しないよう TearDown で戻すこと)。
    public static void SetProjectorModeForTest(bool on)
    {
        projectorModeCache = on;
    }

    // テスト用: キャッシュを捨てて、次回は PlayerPrefs から読み直す。
    public static void ResetProjectorModeCacheForTest()
    {
        projectorModeCache = null;
    }
}
