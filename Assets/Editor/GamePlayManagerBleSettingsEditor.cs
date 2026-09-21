#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

// 既存GamePlayManager InspectorのAuto Startを、Menuより前に読む全Scene共通設定と同期する。
[CustomEditor(typeof(GamePlayManager))]
public sealed class GamePlayManagerBleSettingsEditor : Editor
{
    const string SettingsPath = "Assets/Resources/ImuBleServiceSettings.asset";

    public override void OnInspectorGUI()
    {
        bool changed = DrawDefaultInspector();
        if (changed)
        {
            SyncAutoStartSetting();
        }

        EditorGUILayout.HelpBox(
            "Auto Start Ble Bridge is a game-wide setting. When enabled, BLE starts in Title/Menu and remains active across scene changes.",
            MessageType.Info);
    }

    void SyncAutoStartSetting()
    {
        SerializedProperty gameSetting = serializedObject.FindProperty("autoStartBleBridge");
        ImuBleServiceSettings settings =
            AssetDatabase.LoadAssetAtPath<ImuBleServiceSettings>(SettingsPath);
        if (gameSetting == null || settings == null) return;

        var settingsObject = new SerializedObject(settings);
        SerializedProperty persistentSetting =
            settingsObject.FindProperty("autoStartBleBridge");
        if (persistentSetting == null || persistentSetting.boolValue == gameSetting.boolValue) return;

        persistentSetting.boolValue = gameSetting.boolValue;
        settingsObject.ApplyModifiedProperties();
        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssetIfDirty(settings);
    }
}
#endif
