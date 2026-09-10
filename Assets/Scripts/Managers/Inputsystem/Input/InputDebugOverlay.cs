using UnityEngine;

// Kept for scene compatibility. The game never renders an input debug overlay.
public sealed class InputDebugOverlay : MonoBehaviour
{
    void Awake()
    {
        enabled = false;
    }
}
