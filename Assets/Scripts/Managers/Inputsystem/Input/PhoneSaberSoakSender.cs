using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// 描画が30fpsでも各色60packet/s。Unity APIには触れず、終了時にsocketとthreadを必ず片付ける。
public sealed class PhoneSaberSoakSender : IDisposable
{
    readonly ManualResetEvent stop = new ManualResetEvent(false);
    readonly Thread thread;
    public PhoneSaberSoakSender(int redPort, int bluePort, double seconds, Action<Exception> failed)
    {
        thread = new Thread(() => Send(redPort, bluePort, seconds, failed)) { IsBackground = true, Name = "PhoneSaber soak UDP" };
        thread.Start();
    }

    void Send(int redPort, int bluePort, double seconds, Action<Exception> failed)
    {
        try
        {
            using (var socket = new UdpClient())
            {
                var clock = Stopwatch.StartNew();
                double next = 0;
                while (!stop.WaitOne(0) && clock.Elapsed.TotalSeconds < seconds)
                {
                    double now = clock.Elapsed.TotalSeconds;
                    if (now < next) { stop.WaitOne(Math.Max(1, (int)((next - now) * 1000))); continue; }
                    SendColour(socket, redPort, PhoneSaberSoakModel.Payload(now, seconds, false));
                    SendColour(socket, bluePort, PhoneSaberSoakModel.Payload(now, seconds, true));
                    // 遅延時に過去packetをまとめて流さない。
                    next = PhoneSaberSoakModel.NextPacket(next, now);
                }
            }
        }
        catch (Exception error) { failed(error); }
    }

    static void SendColour(UdpClient socket, int port, string payload)
    {
        if (payload == null) return;
        byte[] bytes = Encoding.ASCII.GetBytes(payload);
        socket.Send(bytes, bytes.Length, "127.0.0.1", port);
    }

    public void Dispose() { stop.Set(); thread.Join(); stop.Dispose(); }
}
