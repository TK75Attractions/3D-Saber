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
            // 0〜camSize → -1〜1 に正規化
            float normalizedX = (x / camWidth) * 0.7f - 1f;
            float normalizedY = -((y / camHeight) * 0.7f - 1f);

            NormalizedPosition = new Vector2(normalizedX, normalizedY);
        }
    }

    void OnDestroy()
    {
        // スレッド終了
        receiveThread?.Abort();
        udpClient?.Close();
    }
}