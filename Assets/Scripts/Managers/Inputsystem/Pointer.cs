using UnityEngine;

public class Pointer : MonoBehaviour
{
    RectTransform rt;

    void Start()
    {
        rt = GetComponent<RectTransform>();
    }

    void Update()
    {
        if (InputPoint.Instance == null) return;

        rt.anchoredPosition = InputPoint.Instance.LocalPosition;
    }
}