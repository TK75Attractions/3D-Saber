using System;
using System.Collections.Generic;

namespace Saber.ChartEditor
{
    /// <summary>
    /// 譜面エディターで開ける裏譜面。本編の表(RushESecretState.Charts)のうち、譜面ファイル chart_<小文字>.json がある曲のもの。
    /// 裏譜面は生成の道具(gen_*.py)で作るので、エディターではノーツの配置を直すだけにする
    /// (新規・複製・難易度の派生・曲の設定の写しには使わない)。
    /// </summary>
    public static class SaberChartSecrets
    {
        // テストでは架空の曲を表の裏譜面に見立てる(null なら本編の表)。
        internal static Func<string, SecretChart> tableOverride;

        // 札のツールチップに出す呼び名(表示だけ。表に足された文字で呼び名が無ければ、文字だけを出す)。
        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "E", "Hard の上" }, { "G", "全部金" }, { "D", "避・照準" }, { "M", "覚える" }, { "W", "風" },
            { "S", "緩急" }, { "V", "消える" }, { "T", "闘牛" }, { "Y", "闇と灯り" }, { "F", "上から降る" },
            { "R", "鏡門" }, { "L", "全部ロング" }, { "P", "追い越し" }, { "Q", "試験用紙" },
        };

        /// <summary>その曲の裏譜面。表に無い曲と、譜面ファイルが無い曲は null。</summary>
        public static SecretChart For(string songId)
        {
            string song = songId?.Trim();
            if (!SaberChartFileStore.IsValidSongId(song, out _)) return null;
            SecretChart secret = tableOverride != null ? tableOverride(song) : RushESecretState.For(song);
            if (secret == null || string.IsNullOrWhiteSpace(secret.Difficulty)) return null;
            return SaberChartFileStore.ChartExists(song, Value(secret)) ? secret : null;
        }

        /// <summary>エディターの難易度の値(ファイル名と同じ小文字。例 "r")。</summary>
        public static string Value(SecretChart secret) => secret?.Difficulty?.Trim().ToLowerInvariant();

        /// <summary>難易度の値が裏譜面の文字か(easy・normal・hard は違う)。</summary>
        public static bool IsSecret(string difficulty) => RushESecretState.IsSecretDifficulty(difficulty);

        /// <summary>札の文字(大文字1字。例 "R")。</summary>
        public static string Letter(string difficulty) => (difficulty ?? string.Empty).Trim().ToUpperInvariant();

        /// <summary>画面の難易度名(例「裏 R」)。</summary>
        public static string Label(string difficulty) => "裏 " + Letter(difficulty);

        /// <summary>「R（鏡門）」の形。呼び名が無ければ文字だけ。</summary>
        public static string Title(string difficulty)
        {
            string letter = Letter(difficulty);
            return Names.TryGetValue(letter, out string name) ? $"{letter}（{name}）" : letter;
        }
    }
}
