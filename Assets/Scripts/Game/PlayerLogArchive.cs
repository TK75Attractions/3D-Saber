using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

// 起動のたびに、1つ前の起動の Unity のログ(Player-prev.log)を PhoneSaber/archive/player へ時刻つきの名前で写す(2026-10-30 夜、改善案⑥)。
// Unity は1つ前の起動のログしか残さない。10/30 は同期のための起動し直しで、チュートリアルの結果(Player.log にしか無い)が消えた。
// 名前は Player-<前の起動のログを最後に書いた時刻 UTC>.log(events の archive と同じ時刻の書き方)。新しい KeepCount 本を残し、古いものは消す。
// events の archive は容量で古いものから消すので、別のフォルダ(archive/player)に置く。
// 本番のビルドだけ(Editor とテストの起動では写さない)。写すのは裏のスレッドで、起動を待たせない。
public static class PlayerLogArchive
{
    public const int KeepCount = 20;
    public const string Prefix = "Player-";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void ArchiveAtStartup()
    {
        if (Application.isEditor || Array.IndexOf(Environment.GetCommandLineArgs(), RankingSync.TestRunArgument) >= 0) return;
        string log = Application.consoleLogPath;
        if (string.IsNullOrEmpty(log)) return;
        // Unity の API はメインスレッドで読んでから渡す。
        string previous = Path.Combine(Path.GetDirectoryName(log), "Player-prev.log");
        string archive = Path.Combine(Application.persistentDataPath, "PhoneSaber", "archive", "player");
        Task.Run(() =>
        {
            try { Archive(previous, archive, KeepCount); }
            catch (Exception) { /* 写せなくてもゲームはそのまま動く。 */ }
        });
    }

    // previous を archive へ写し、写した先(previous が無ければ null)を返す。同じ名前がもうあれば写さない(同じ起動を2回写さない)。
    public static string Archive(string previous, string archive, int keep)
    {
        if (!File.Exists(previous)) return null;
        Directory.CreateDirectory(archive);
        string name = Prefix + File.GetLastWriteTimeUtc(previous).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log";
        string target = Path.Combine(archive, name);
        if (!File.Exists(target)) File.Copy(previous, target);
        Prune(archive, keep);
        return target;
    }

    // 名前が時刻の順なので、名前で並べて古い方から消し、新しい keep 本を残す。
    public static void Prune(string archive, int keep)
    {
        var files = Directory.GetFiles(archive, Prefix + "*.log");
        Array.Sort(files, StringComparer.Ordinal);
        for (int i = 0; i < files.Length - Math.Max(0, keep); i++)
        {
            try { File.Delete(files[i]); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
