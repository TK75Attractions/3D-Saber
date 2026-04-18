using UnityEngine;

public class HapticTest : MonoBehaviour
{
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            Haptic.On();
        }

        if (Input.GetKeyUp(KeyCode.Space))
        {
            Haptic.Off();
        }

        if (Input.GetKeyUp(KeyCode.A))
        {
            Haptic.Vibrate(0.5f);
        }
    }
}