using UnityEngine;

public class Pointer : MonoBehaviour
{
    public enum PointerSource
    {
        Stick1,
        Stick2
    }

    [SerializeField] private PointerSource source = PointerSource.Stick1;
    [SerializeField] private bool followRotation = true;
    [SerializeField] private bool followStickLength = true;
    [SerializeField] private float minVisualLength = 40f;

    RectTransform rt;

    void Start()
    {
        rt = GetComponent<RectTransform>();
    }

    void Update()
    {
        if (InputPoint.Instance == null) return;

        rt.anchoredPosition = source == PointerSource.Stick2
            ? InputPoint.Instance.LocalPosition2
            : InputPoint.Instance.LocalPosition;

        if (followRotation)
        {
            float angle = source == PointerSource.Stick2
                ? InputPoint.Instance.LocalAngleDeg2
                : InputPoint.Instance.LocalAngleDeg;
            rt.localRotation = Quaternion.Euler(0f, 0f, angle);
        }

        if (followStickLength)
        {
            float length = source == PointerSource.Stick2
                ? InputPoint.Instance.LocalStickLength2
                : InputPoint.Instance.LocalStickLength;
            length = Mathf.Max(length, minVisualLength);
            rt.sizeDelta = new Vector2(length, rt.sizeDelta.y);
        }
    }
}