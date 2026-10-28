using System;
using UnityEngine;

// 運営モード(2026-10-29 ユーザー「運営モードにどっかのFキーで移行できるようにし、それを使って運営したい」)。
// 入れると、ランキング(通算の順位・HI-SCORE・曲ごとの達成)と実績が本番用の表(名前空間 "fes"、2台で共有)に切り替わる。
// 切っているとき(既定)は今までどおりの表(名前空間 "")。切り替えは F12(OperatorModeHotkey)。保存して、次の起動でもそのまま。
// 1回のプレイがどちらの表に残るかは、曲を始めたときに決める(GameSession.RunNamespace)。
public static class OperatorMode
{
    public const string PreferenceKey = "OperatorMode.v1";
    public const string FestivalNamespace = "fes";
    public const string OrdinaryNamespace = "";

    static bool? onForTest;
    static string keyForTest;

    // 切り替えたとき(「運営」の札など)。
    public static event Action Changed;

    // 保存先の鍵(テストでは UseKeyForTest で差し替える)。
    public static string Key => keyForTest ?? PreferenceKey;

    // 遊んでいない間(EditMode のテスト・エディターの道具)は、本番の保存値(OperatorMode.v1)を読まない・書かない(切っている扱い)。
    // テストのまとめの既定(SetUpFixture)は、Unity では一部のテストにしか効かないため(2026-10-29 に確かめた)。
    static bool ProductionKeyBlocked => keyForTest == null && !Application.isPlaying;

    public static bool IsOn => onForTest ?? (!ProductionKeyBlocked && PlayerPrefs.GetInt(Key, 0) == 1);

    static string festivalForTest;

    // 今の名前空間。表示する所(選曲の TOP 5 など)はこれ、記録する所は GameSession.RunNamespace を使う。
    public static string Namespace => IsOn ? (festivalForTest ?? FestivalNamespace) : OrdinaryNamespace;

    // テスト用: 入れたときの名前空間を差し替える(本番の fes_ の表に触れずに、表示の切り替えを確かめる)。null で "fes"。
    public static void UseFestivalNamespaceForTest(string ns)
    {
        if (ns != null && (ns.Length == 0 || !IsValidNamespace(ns))) throw new ArgumentException("名前空間の形が違う: " + ns);
        string before = Namespace;
        festivalForTest = ns;
        if (Namespace != before) Changed?.Invoke();
    }

    public static void Toggle() => Set(!IsOn);

    public static void Set(bool on)
    {
        if (on == IsOn) return;
        if (onForTest.HasValue || ProductionKeyBlocked) onForTest = on;
        else
        {
            PlayerPrefs.SetInt(Key, on ? 1 : 0);
            PlayerPrefs.Save();
        }
        Changed?.Invoke();
    }

    // テスト用: true / false の間は PlayerPrefs を読み書きしない(切り替えも保存しない)。null で保存値へ戻す。
    public static void OverrideForTest(bool? on)
    {
        bool before = IsOn;
        onForTest = on;
        if (IsOn != before) Changed?.Invoke();
    }

    // テスト用: 保存先の鍵を差し替える(null で本番の鍵。AchievementStore.UseKeyForTest と同じ形)。本番の OperatorMode.v1 に触れずに保存を確かめる。
    public static void UseKeyForTest(string key)
    {
        bool before = IsOn;
        keyForTest = string.IsNullOrEmpty(key) || key == PreferenceKey ? null : key;
        if (IsOn != before) Changed?.Invoke();
    }

    // 保存の鍵の頭。"" は今までと同じ鍵(頭なし)、ほかは "fes_" のように名前空間と "_"。
    public static string KeyPrefix(string ns) => string.IsNullOrEmpty(ns) ? "" : ns + "_";

    public const int MaxNamespaceLength = 8;

    // 名前空間の形: ""(null も)か、1〜8 文字の小文字の英数字(相手の台から届いた記録の検査にも使う)。
    public static bool IsValidNamespace(string ns)
    {
        if (string.IsNullOrEmpty(ns)) return true;
        if (ns.Length > MaxNamespaceLength) return false;
        foreach (char c in ns)
            if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9')) return false;
        return true;
    }
}
