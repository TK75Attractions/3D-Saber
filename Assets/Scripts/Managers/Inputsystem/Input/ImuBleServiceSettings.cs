using UnityEngine;

// Resources assetに集約し、Menu時点でGame sceneを読まずに設定を参照する。
public sealed class ImuBleServiceSettings : ScriptableObject
{
    const string ResourceName = "ImuBleServiceSettings";
    [SerializeField] bool autoStartBleBridge = true;

    public static bool AutoStartBleBridge
    {
        get
        {
            var settings = Resources.Load<ImuBleServiceSettings>(ResourceName);
            // 設定asset消失時は勝手にprocessを起動しない。
            return settings != null && settings.autoStartBleBridge;
        }
    }
}
