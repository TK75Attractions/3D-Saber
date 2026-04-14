using UnityEngine;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public class InputPoint : MonoBehaviour
{
    UdpClient udpClient;
    Thread receiveThread;
    public int port = 5005;

    private float x, y;
    private bool hasNewData = false;

    // 画面サイズ（Pythonのカメラ解像度に合わせる）
    public float camWidth = 1920f;
    public float camHeight = 1080f;

    void Start()
    {
        udpClient = new UdpClient(port);
        receiveThread = new Thread(ReceiveData);
        receiveThread.IsBackground = true;
        receiveThread.Start();
    }

    void ReceiveData()
    {
        while (true)
        {
            IPEndPoint endPoint = new IPEndPoint(IPAddress.Any, port);
            byte[] data = udpClient.Receive(ref endPoint);
            string message = Encoding.UTF8.GetString(data);

            string[] parts = message.Split(',');
            if (parts.Length == 2)
            {
                x = float.Parse(parts[0]);
                y = float.Parse(parts[1]);
                hasNewData = true;
            }
        }
    }

    void Update()
    {
        if (hasNewData)
        {
            // 座標を-1〜1に正規化
            float normalizedX = (x / camWidth)*0.7f  - 1f ;
            float normalizedY = -((y / camHeight)*0.7f - 1f);

            // このオブジェクトの位置に反映（スケールは適宜調整）
            transform.position = new Vector3(normalizedX * 5f, normalizedY * 5f, 0f);
            hasNewData = false;
        }
    }

    void OnDestroy()
    {
        receiveThread?.Abort();
        udpClient?.Close();
    }
}
