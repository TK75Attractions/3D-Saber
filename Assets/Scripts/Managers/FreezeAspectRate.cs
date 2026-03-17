using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/*
 * FreezeAspectRate
 * - 画面比率を `aspect` に固定し、余白部分をスプライトで埋めるカメラ管理スクリプトです。
 * - `aspect` は、画面の幅と高さの比率を表す Vector2Int で、デフォルトは 16:9 に設定されています。
 * - `colorbase` は、余白部分のスプライトの色を指定する Color32 で、デフォルトは黒色に設定されています。
 * - これはモニターの画面比率が異なる場合に、ゲームの表示が崩れないようにするためのスクリプトです。(あんまりいじらなくていいよ)
 */


[ExecuteInEditMode]
public class FreezeAspectRate : MonoBehaviour
{
    public Vector2Int aspect = new Vector2Int(16,9);
    public Color32 colorbase = Color.black;
    [SerializeField] private float aspectRate;
    [SerializeField] private float cameraSize = 0;
    [SerializeField] private float oldSize = 0;
    [SerializeField] private float setTime = 0;
    [SerializeField] private Camera main;
    [SerializeField] private Camera backCamera;
    [SerializeField] private Camera UICamera;
    [SerializeField] private Camera frontCamera;
    [SerializeField] private Camera backImageCamera;
    [SerializeField] private Sprite Sup;
    [SerializeField] private Sprite Sdown;
    [SerializeField] private Sprite Sright;
    [SerializeField] private Sprite Sleft;
    [SerializeField] private Transform up, down, right, left;

    public void Awake()
    {
        aspectRate = (float)aspect.x / aspect.y;
        main = GetComponent<Camera>();
        backCamera = transform.parent.parent.Find("BackCamera").GetComponent<Camera>();
        UICamera = transform.parent.Find("UICamera").GetComponent<Camera>();
        frontCamera = transform.parent.Find("FrontCamera").GetComponent<Camera>();
        backImageCamera = transform.parent.Find("BackImageCamera").GetComponent<Camera>();

        CreateBackCamera();
        UpdateScreenRate();
    }

    private void Update()
    {
        ChangeSize();

        if (IsChangeAspect()) return;
        UpdateScreenRate();
        main.ResetAspect();
    }

    private void CreateBackCamera()
    {
#if UNITY_EDITOR
        if (!UnityEditor.EditorApplication.isPlaying) return;

#endif
        Debug.Log("Set BackCamera");
        backCamera.transform.position = new Vector3(0, 0, -5);
        backCamera.depth = -99;
        backCamera.orthographic = true;
        backCamera.fieldOfView = 0;
        backCamera.farClipPlane = 10;
        backCamera.nearClipPlane = 1;
        backCamera.depthTextureMode = DepthTextureMode.None;
        backCamera.renderingPath = RenderingPath.VertexLit;
        backCamera.useOcclusionCulling = false;
        up.GetComponent<SpriteRenderer>().sprite = Sup;
        down.GetComponent<SpriteRenderer>().sprite = Sdown;
        right.GetComponent<SpriteRenderer>().sprite = Sright;
        left.GetComponent<SpriteRenderer>().sprite = Sleft;
        up.GetComponent<SpriteRenderer>().color = colorbase;
        down.GetComponent<SpriteRenderer>().color = colorbase;
        right.GetComponent<SpriteRenderer>().color = colorbase;
        left.GetComponent<SpriteRenderer>().color = colorbase;
    }

    private void UpdateScreenRate()
    {
        if (aspect.x <= 0 || aspect.y <= 0) return;
        if (Screen.width <= 0 || Screen.height <= 0) return;
        if (main == null || UICamera == null || frontCamera == null || backImageCamera == null) return;
        if (up == null || down == null || right == null || left == null) return;

        aspectRate = (float)aspect.x / aspect.y;
        float baseAspect = (float)aspect.y / aspect.x;
        float nowAspect = (float)Screen.height / Screen.width;

        if (float.IsNaN(baseAspect) || float.IsInfinity(baseAspect)) return;
        if (float.IsNaN(nowAspect) || float.IsInfinity(nowAspect)) return;
        
        if (baseAspect > nowAspect)
        {
            float change = nowAspect / baseAspect;
            Rect set = new Rect((1 - change) * 0.5f, 0, change, 1);
            main.rect = set;
            UICamera.rect = set;
            frontCamera.rect = set;
            backImageCamera.rect = set;

            float h = main.orthographicSize * 2;
            float w = h * aspectRate;
            float x = (h + w) / 2f;
            right.localScale = new Vector3(h, h, 0);
            right.position = new Vector3(x, 0, 0);
            left.localScale = new Vector3(h, h, 0);
            left.position = new Vector3(-x, 0, 0);
            up.localScale = new Vector3(w, w, 0);
            up.position = new Vector3(0, x, 0);
            down.localScale = new Vector3(w, w, 0);
            down.position = new Vector3(0, -x, 0);
        }
        else
        {
            float change = baseAspect / nowAspect;

            Rect set = new Rect(0, (1 - change) * 0.5f, 1, change);
            main.rect = set;
            UICamera.rect = set;
            frontCamera.rect = set;
            backImageCamera.rect = set;

            float h = change * main.orthographicSize * 2f;
            float w = h * aspectRate;
            float x = (h + w) / 2f;
            
            right.localScale = new Vector3(h, h, 0);
            right.position = new Vector3(x, 0, 0);
            left.localScale = new Vector3(h, h, 0);
            left.position = new Vector3(-x, 0, 0);
            up.localScale = new Vector3(w, w, 0);
            up.position = new Vector3(0, x, 0);
            down.localScale = new Vector3(w, w, 0);
            down.position = new Vector3(0, -x, 0);
        }
    }

    private bool IsChangeAspect() => main.aspect == aspectRate;

    private void ChangeSize()
    {
        if (cameraSize == oldSize) return;

        setTime += Time.deltaTime;
        float f;
        if (setTime < 0.4f) f = oldSize + (cameraSize - oldSize) * setTime / 0.4f;
        else
        {
            f = cameraSize;
            oldSize = cameraSize;
        }
        
        main.orthographicSize = f;
        UICamera.orthographicSize = f;
        frontCamera.orthographicSize = f;
        backCamera.orthographicSize = f;
    }

    public void SetCameraSize(float f)
    {
        if (f > 0)
        {
            oldSize = cameraSize;
            cameraSize = f;
            setTime = 0;
        }
    }

    public void SetCameraSizeImediately(float f)
    {
        if(f > 0)
        {
            cameraSize = f;
            oldSize = f;
            main.orthographicSize = f;
            UICamera.orthographicSize = f;
            frontCamera.orthographicSize = f;
            backCamera.orthographicSize = f;
        }
    }

    public Camera GetUICamera() => UICamera;
}