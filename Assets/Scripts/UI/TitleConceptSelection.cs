using System;
using UnityEngine;

// タイトルを開くたび背景を抽選する。撮影・テストの指定だけ次の表示1回に適用する。
public static class TitleConceptSelection
{
    public const int Count = 4;
    static readonly System.Random random = new System.Random();
    static int current = -1;
    static int? nextPreview = InitialPreview();
    public static int Current => current;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetSession()
    {
        current = -1;
        nextPreview = InitialPreview();
    }

    static int? InitialPreview()
    {
        if (!Application.isBatchMode) return null;
        string[] args = Environment.GetCommandLineArgs();
        if (Array.IndexOf(args, "-titleAllConcepts") >= 0) return 0;
        int index = Array.IndexOf(args, "-titleConcept");
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int value)
            && value >= 0 && value < Count ? value : (int?)null;
    }

    public static int BeginTitle()
    {
        if (nextPreview.HasValue)
        {
            current = nextPreview.Value;
            nextPreview = null;
        }
        else
        {
            // 前回と同じ背景が続かないよう、残りの候補から均等に選ぶ。
            int chosen = random.Next(current >= 0 ? Count - 1 : Count);
            current = current >= 0 && chosen >= current ? chosen + 1 : chosen;
        }
        return current;
    }

    public static void Select(int value)
    {
        if (value < 0 || value >= Count) throw new ArgumentOutOfRangeException(nameof(value));
        nextPreview = value;
    }

    public static void Build(Transform parent)
    {
        switch (current)
        {
            case 1: TitleConceptA.BuildBackground(parent); break;
            case 2: TitleConceptB.BuildBackground(parent); break;
            case 3: TitleConceptC.BuildBackground(parent); break;
        }
    }
}
