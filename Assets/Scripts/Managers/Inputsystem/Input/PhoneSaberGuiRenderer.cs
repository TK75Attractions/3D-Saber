using System;
using UnityEngine;

// キー入力・受信監視は所有者に残し、非表示時は OnGUI 自体の呼出しを止める。
// シーンや prefab を変更せず、所有者と同じ GameObject に実行時に追加する。
public class PhoneSaberGuiRenderer : MonoBehaviour
{
    Action draw;

    public void Initialize(Action drawGui, bool usesLayout)
    {
        enabled = false;
        draw = drawGui;
        useGUILayout = usesLayout;
    }

    protected void OnGUI() => draw?.Invoke();
}
