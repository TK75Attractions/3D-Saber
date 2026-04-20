using UnityEngine;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public class InputPoint : MonoBehaviour
{
    // Singleton（どこからでもアクセスするため）
    public static InputPoint Instance { get; private set; }

    UdpClient udpClient;
    Thread receiveThread;
    public int port = 5005;

    // 生データ（スレッドで更新される）
    float rawX, rawY;
    bool hasNewData = false;

    // 他スクリプトが読む用（正規化済み）
    public Vector2 NormalizedPosition { get; private set; }

    // スレッド同期用
    object lockObj = new object();

    // カメラ解像度
    public float camWidth = 1920f;
    public float camHeight = 1080f;
    public RectTransform boardRect;
    public Vector2 LocalPosition { get; private set; }

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        // UDP受信開始
        udpClient = new UdpClient(port);

        // 別スレッドで受信処理
        receiveThread = new Thread(ReceiveData);
        receiveThread.IsBackground = true;
        receiveThread.Start();
    }

    void ReceiveData()
    {
        while (true)
        {
            // データ受信
            IPEndPoint endPoint = new IPEndPoint(IPAddress.Any, port);
            byte[] data = udpClient.Receive(ref endPoint);
            string message = Encoding.UTF8.GetString(data);

            // "x,y" 形式を想定
            string[] parts = message.Split(',');

            if (parts.Length == 2)
            {
                float x = float.Parse(parts[0]);
                float y = float.Parse(parts[1]);

                // メインスレッドと衝突しないようロック
                lock (lockObj)
                {
                    rawX = x;
                    rawY = y;
                    hasNewData = true;
                }
            }
        }
    }

    void Update()
    {
        float x = 0, y = 0;
        bool updated = false;

        // スレッドから受け取った値をコピー
        lock (lockObj)
        {
            if (hasNewData)
            {
                x = rawX;
                y = rawY;
                hasNewData = false;
                updated = true;
            }
        }

        if (updated)
        {
    // カメラ座標 → 画面座標に変換
            float screenX = (x / camWidth) * Screen.width;
            float screenY = (y / camHeight) * Screen.height;

            Vector2 screenPos = new Vector2(screenX, screenY);

            // 画面座標 → boardRectのローカル座標
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
            boardRect,
            screenPos,
            null,
            out Vector2 localPos
            );

            LocalPosition = localPos;
        }
    }

    void OnDestroy()
    {
        // スレッド終了
        receiveThread?.Interrupt(); // Abortより安全
        udpClient?.Close();
    }
}