using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// 当日用の Player ビルド。PhoneSaber/mac・windows の起動スクリプトが参照する Builds/ へ出力する。
// Editor のメニュー、または batchmode の -executeMethod から使う（Editor を開いたままの複製プロジェクトでも可）。
//   Unity -batchmode -quit -projectPath <path> -executeMethod PhoneSaberPlayerBuild.BuildMac -logFile build.log
public static class PhoneSaberPlayerBuild
{
    [MenuItem("Tools/PhoneSaber/Build/macOS Player")]
    public static void BuildMac() => Build(BuildTarget.StandaloneOSX, "Builds/Mac/3D-Saber.app");

    [MenuItem("Tools/PhoneSaber/Build/Windows Player")]
    public static void BuildWindows() => Build(BuildTarget.StandaloneWindows64, "Builds/Windows/3D-Saber.exe");

    static void Build(BuildTarget target, string path)
    {
        // Build Profiles の Scene List（有効なもの）をそのまま使う。
        string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = path,
            target = target,
            targetGroup = BuildTargetGroup.Standalone,
            options = BuildOptions.None,
        };
        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;
        Debug.Log($"[PhoneSaber] build {target} -> {path}: {summary.result} " +
                  $"size={summary.totalSize} time={summary.totalTime} errors={summary.totalErrors}");
        if (Application.isBatchMode) EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
