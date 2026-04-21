using UnityEngine;

// 本編シーンで 30fps キャップ等を確実に外す。
// GManager が targetFrameRate=30 を設定するため、本編では必ず入れる。
public class UnlockFrameRate : MonoBehaviour
{
    public int targetFrameRate = -1;
    public int vSyncCount = 0;

    void Awake()
    {
        Application.targetFrameRate = targetFrameRate;
        QualitySettings.vSyncCount = vSyncCount;
    }
}
