using System;
using UnityEngine;
using UnityEngine.InputSystem;

// タイトルだけで候補を切り替える。比較中の選択はアプリ終了まで保持し、設定ファイルは変更しない。
public static class TitleConceptSelection
{
    public const int Count = 4;
    static int current = InitialSelection();
    public static int Current => current;

    static int InitialSelection()
    {
        if (!Application.isBatchMode) return 0;
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-titleConcept");
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int value)
            && value >= 0 && value < Count ? value : 0;
    }

    public static void Select(int value)
    {
        if (value < 0 || value >= Count) throw new ArgumentOutOfRangeException(nameof(value));
        current = value;
    }

    public static bool ReadSelectionKeys()
    {
        var keys = Keyboard.current;
        if (keys == null) return false;
        int requested = keys.digit0Key.wasPressedThisFrame || keys.numpad0Key.wasPressedThisFrame ? 0
            : keys.digit1Key.wasPressedThisFrame || keys.numpad1Key.wasPressedThisFrame ? 1
            : keys.digit2Key.wasPressedThisFrame || keys.numpad2Key.wasPressedThisFrame ? 2
            : keys.digit3Key.wasPressedThisFrame || keys.numpad3Key.wasPressedThisFrame ? 3 : -1;
        if (requested < 0 || requested == current) return false;
        Select(requested);
        return true;
    }

    public static void Build(Transform parent)
    {
        switch (current)
        {
            case 1: TitleConceptA.Build(parent); break;
            case 2: TitleConceptB.Build(parent); break;
            case 3: TitleConceptC.Build(parent); break;
        }
    }
}
