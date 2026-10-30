using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

// 台同士のランキング同期(2026-10-28 ユーザー「台同士でなんとかしてランキングを同期できない？本番リアルタイムに」)。
// 台A・台B(Windows / Mac のビルド)が同じネットワーク(学校の Wi-Fi・片方のホットスポット)にいるとき、遊び終わった回を
// 相手の台へその場で(1秒以内)届け、通算順位の表(DailyRankingStore)と上位5件(HighScoreStore)を両方の台で同じにする。
// 曲ごとの達成(SongAchievementStore)は合わせない。記録の形・検査・束の作り方は RankingSyncLog。
// ・運営モード(2026-10-29、通信の版 2): 1回の記録の ns(名前空間)の表へ足す("" はふだん、"fes" は本番)。ログ・足し直し待ちは1つのまま。
//   記録の ach(その回の実績)は AchievementStore.Merge で数に足す(2台の「○人目」)。ほかの版の相手が聞こえたら、運営に1回知らせて合流しない。
// ・UDP 5017。受信と送信はそれぞれ別スレッド(受信はキューに入れるだけ、送信は上限つきのキューから送るだけ)。
//   解析と PlayerPrefs への読み書きはメインスレッド(Update)だけ。止めるときは停止の合図・ソケットを閉じる・Join(Thread.Abort は使わない)。
// ・自分の台で遊び終わったら(GameSession.RecordCompletedDailyRanking → RecordLocal)、その回をすぐ送る(取りこぼしに備えて 0・0.3・1.0 秒の3回)。
// ・5秒ごとに要約(件数と id のハッシュ、最近聞こえた相手)を送る。相手の要約が自分と違えば、全部の記録を束で送る
//   (同じ相手へは3秒に1回まで。1コマで全部を作らず、少しずつ作って送る)。つながっていない間の記録も、これで合わせる。
// ・受け取った回は、表へ足す待ちに入れ、曲×難易度ごとにまとめて足す(表の読み書きは1回、1コマの上限つき)。
//   新しい1回(遊び終わった直後に届く1回)は、全部の記録の束より先に、曲の演奏中でもすぐ足す。
//   束の併合・全部の記録の送信・ディスクへの保存は、曲の演奏中(Game シーン)は止め、結果・選曲・タイトルの間にまとめて行う。
// ・表に書けなかった回(相手の回・自分の回とも)は、足せなかった表だけを足し直し待ちに残し、動いている間に足し直す。
//   相手の回はログに入れず、別のキー(RankingSync.Pending.v1)にも残す(終了しても失われない)。起動時には、ログの各回が表にあるかも確かめる。
// ・起動時(本番のビルドだけ)には逆に、この台の通算の表にあってログに無い回(同期が入る前の版などで遊んだ回)を、この台の回としてログに足す
//   (RankingSyncBackfill。2026-10-29 ユーザー「完全に同期しちゃって欲しい」)。要約が変わるので、相手の台へは全部の記録の束で届く。
// ・送り先は、動いている IPv4 のネットワークごとのブロードキャスト・255.255.255.255・届いた相手・手で入れた相手
//   (PlayerPrefs RankingSync.Peers のカンマ区切り、または起動引数 -rankingPeer <IP>)。
// ・つながりは双方向で確かめる(相手の要約に、こちらの機械 ID が入っていたら「つながりました」)。見つからない・片方向・途切れ・
//   ポートを開けない・送れないときは、運営の知らせ(上の1行)を1回ずつ出す。ゲームは今までどおり動く(同期だけしない)。
// ・Editor では既定で動かさない(開発中のプレイを本番の台へ流さないため。Tools > Ranking Sync で入れる)。テストでは作らない。
public sealed class RankingSync : MonoBehaviour
{
    public const int DefaultPort = 5017;
    public const string MachinePreferenceKey = "RankingSync.Machine.v1";
    public const string PeersPreferenceKey = "RankingSync.Peers";
    // 表に書けずに足し直しを待っている、相手の台の回(ログにはまだ入れていない)。
    public const string PendingPreferenceKey = "RankingSync.Pending.v1";
    public const string PeerArgument = "-rankingPeer";
    // -rankingSync off で止める(本番で困ったとき)。
    public const string SwitchArgument = "-rankingSync";
    // Unity のテストを回す起動(コマンドラインのテスト)では作らない。
    public const string TestRunArgument = "-runTests";
    public const double DigestSeconds = 5;
    public const double BundleSeconds = 3;
    public const double LostSeconds = 60;
    // ポートを開けてからこの秒数のあいだ相手が一度も見つからなければ知らせる。
    public const double NoPeerSeconds = 30;
    // 相手から届き始めてからこの秒数のあいだ、相手がこちらを聞こえていると分からなければ「片方向」と知らせる。
    public const double OneWaySeconds = 15;
    public const double PendingRetrySeconds = 10;
    public const float NoticeSeconds = 6f;
    public static readonly double[] SendDelays = { 0, .3, 1 };
    // 曲を演奏するシーン(この間は束の併合・全部の記録の送信・ディスクへの保存を止める)。
    const string GameSceneName = "Game";
    // 試験の起動(smoke・soak・遅延と追従の計測)では動かさない。試験の回を相手の台へ送らないため。
    static readonly string[] DiagnosticArguments =
    {
        PhoneSaberPlayerSmoke.Argument, PhoneSaberSoakModel.Argument, PhoneSaberLatencyProbe.AutoArgument, PhoneSaberTrackingProbe.Argument,
    };
    // Test Runner(Unity のテストを回す仕組み)が Play のシーンに置く物の名前。Test Runner の窓から回したテストでも作らない。
    const string TestRunnerObjectName = "Code-based tests runner";
    const double AutoStartDelaySeconds = 1;
    const double BindRetrySeconds = 10;
    const double ReceiverRestartSeconds = 2;
    const double BroadcastRefreshSeconds = 15;
    const double SaveIntervalSeconds = .5;
    const double SaveRetrySeconds = 2;
    // 受信のキュー(新しい1回と要約 / 束)と、1コマに読む通の数・表へ足す回の数・送る通の数の上限
    // (大きく合わせるときも、引っかからないように数コマに分ける)。
    const int FreshInboxCapacity = 1024;
    const int BulkInboxCapacity = 4096;
    const int FreshPerFrame = 64;
    const int BulkPerFrame = 16;
    // 表へ足す待ちがこれより多いあいだは、束を読み進めない(受信のキューで待たせる)。
    const int BulkBacklog = 256;
    const int ApplyPerFrame = 32;
    // 送る待ち(送信スレッドが送る)の上限。全部の記録の束は、送る待ちがこれより少ないときだけ作り足す。
    const int SendQueueCapacity = 1024;
    const int TransferQueueRoom = 64;
    const int SendsPerFrame = 16;
    const int DatagramsPerTransferStep = 4;
    const int MaxPeers = 16;
    const int ReceiveBufferBytes = 2048;
    const int JoinMilliseconds = 1000;
#if UNITY_EDITOR
    const string EditorEnabledKey = "RankingSync.EditorEnabled";
    const string EditorMenu = "Tools/Ranking Sync/Editor でも同期する";
#endif

    // テストでは false にして作らない(Tests/Editor・Tests/PlayMode の SetUpFixture が切り替える)。
    public static bool AutoStartEnabled = true;
    // 起動時に自動で作った1つ(本番)。テストが Create で作ったものはここに入らない。
    public static RankingSync Instance { get; private set; }
    // 自分の台で遊び終わった回が RecordLocal に渡ったとき(テストと確認用。同期が無いときも呼ぶ)。
    public static event Action<RankingPlay> LocalPlayRecorded;

    // 作るときの設定。既定は本番の値。
    public sealed class Settings
    {
        public int Port = DefaultPort;      // 受ける UDP のポート(0 = 空いているもの。テスト用)
        public int PeerPort = DefaultPort;  // ブロードキャストと、ポートを書いていない相手の宛先
        public bool Broadcast = true;       // ブロードキャストで相手を探すか(テストでは false: 本物のネットワークへ流さない)
        public bool LoopbackOnly;           // 127.0.0.1 だけで受けるか(テスト用)
        public bool Offline;                // ネットワークを開かない(テストで、受け取ったあとの処理だけを確かめる)
        public string Peers = "";           // 手で入れた相手(カンマ区切り。「IP」か「IP:ポート」)
        public string MachineId;            // 機械 ID(null なら PlayerPrefs の RankingSync.Machine.v1。無ければ作り、最初に送る直前に保存)
        public string Station = "";         // 台名(A / B)
        public string LogKey = RankingSyncLog.PreferenceKey;
        public string PendingKey = PendingPreferenceKey;
        public bool ShowNotices = true;     // 運営の知らせを画面に出すか(テストでは false。LastNotice には残す)
        public double StartDelaySeconds;    // 動き出す(照合・ネットワーク)までに待つ秒数(0 なら作ってすぐ)
        // 動き出すとき、この台の通算の表にあってログに無い回をこの台の回としてログに足すか(本番のビルドの1つだけ。
        // Editor で同期を入れても、それまでに Editor で遊んだ開発中の回は送らない)。
        public bool Backfill;
        // 調べる表(null なら RankingSyncBackfill.StationCharts: この台の全曲 × 難易度 × ふだん・運営モードの表)。テストで差し替える。
        public IEnumerable<RankingSyncBackfill.Chart> BackfillCharts;
    }

    sealed class Peer
    {
        public string MachineId, Station = "";
        public IPEndPoint EndPoint;
        public double FirstHeardAt, LastHeardAt, LastAckAt = double.NegativeInfinity;
        public double NextBundleAt = double.NegativeInfinity;
        // 新しい相手は「まだ届いていない」から始め、最初の1通で聞こえ始めにする(途切れたあとも同じ)。
        public bool Lost = true;
        public bool Connected, OneWayNoticed;
    }

    sealed class Transfer
    {
        public Peer Peer;
        public int Next;
    }

    // 表へ足す1回と、まだ足していない表(通算の順位 Daily・上位5件 High)。Persist は、相手の回で足し直し待ちを別のキーに残したもの。
    // Chart は同じ表へ書く回をまとめる鍵。Where はいまどの待ちにいるか。Ticket は待ちに入れ直すたびに増やす番号
    // (束の待ちにある回を新しい1回の待ちへ移したとき、束の待ちに残った古い入れ物を飛ばすため)。
    sealed class ApplyItem
    {
        public RankingPlay Play;
        public string Chart;
        public bool Daily, High, Persist;
        public ApplyWhere Where;
        public int Ticket;
    }

    enum ApplyWhere { None, Fresh, Bulk, Reconcile, Pending }

    // 待ちに入れた1つ(入れたときの番号つき)。
    struct Queued { public ApplyItem Item; public int Ticket; }

    struct Inbound { public string Text; public IPEndPoint From; }
    struct Outgoing { public byte[] Bytes; public IPEndPoint Target; public int Plays; }
    struct PendingSend { public double Due; public string Text; }
    // ネットワークごとのブロードキャストを調べた結果(背景で作り、参照だけを差し替える)。
    sealed class BroadcastScan
    {
        public IPAddress[] Addresses = { IPAddress.Broadcast };
        public string Guessed, Unreadable;
    }

    Settings settings = new Settings();
    RankingSyncLog log = new RankingSyncLog();
    RankingSyncLog savedPending = new RankingSyncLog();
    string machineId = "", station = "", selfMarker = "";
    bool configured, autoStarted, activated, stopped, machineIdUnsaved, everHeard, bindFailing;
    bool logDirty, pendingStoreDirty, saveDirty;
    // 1回だけの警告と知らせ
    bool bindWarned, sendWarned, sendNoticed, fullWarned, nearFullNoticed, invalidWarned, conflictWarned, receiveWarned, handleWarned;
    bool saveWarned, idWarned, guessWarned, unreadableWarned, noPeerNoticed, oldPeerNoticed, newerPeerNoticed;
    // 受信スレッドが聞いた、ほかの版の相手の版(0 = まだ)。メインスレッドが1回だけ知らせる。
    int otherVersionHeard;
    readonly HashSet<string> applyWarned = new HashSet<string>(StringComparer.Ordinal);
    double startAt, activatedAt, listeningSince, nextBindAt, nextDigestAt, nextSaveAt, nextBroadcastRefreshAt;
    double pendingRetryAt = double.NegativeInfinity;
    readonly List<IPEndPoint> manualPeers = new List<IPEndPoint>();
    readonly Dictionary<string, Peer> peers = new Dictionary<string, Peer>(StringComparer.Ordinal);
    readonly List<PendingSend> pendingSends = new List<PendingSend>();
    readonly List<Transfer> transfers = new List<Transfer>();
    // 表へ足す待ち: 新しい1回 → 束で届いた回 → 起動時の照合 の順に足す。pending は表に書けずに足し直しを待つ回。
    // waiting は、待ちと足し直し待ちのすべての回(id で引く。同じ回を重ねて入れない)。
    readonly Queue<Queued> freshApply = new Queue<Queued>();
    readonly Queue<Queued> bulkApply = new Queue<Queued>();
    readonly Queue<Queued> reconcileApply = new Queue<Queued>();
    readonly List<ApplyItem> pending = new List<ApplyItem>();
    readonly Dictionary<string, ApplyItem> waiting = new Dictionary<string, ApplyItem>(StringComparer.Ordinal);
    // 別のキーに残していた足し直し待ちのうち、足せてログ(メモリ)に入れた回。ログの保存に成功するまでは別のキーから消さない
    // (ログを書けないまま終わっても、ログにも別のキーにも残らない回を作らない)。
    readonly List<RankingPlay> releasedPending = new List<RankingPlay>();
    BroadcastScan scan = new BroadcastScan();
    int refreshingBroadcasts;
    int? busySceneHandle;
    bool busyScene;

    // 受信スレッドと分け合うもの(2つのキューと droppedMessages は gate の中だけ)。
    readonly object gate = new object();
    readonly Queue<Inbound> freshInbox = new Queue<Inbound>();
    readonly Queue<Inbound> bulkInbox = new Queue<Inbound>();
    int droppedMessages;
    // 送信スレッドと分け合うもの(送る待ちと droppedSends は sendGate の中だけ。数は Interlocked)。
    // 送る待ちは2つ: 新しい1回と要約(先に送る)/全部の記録の束(あとで送る)。
    readonly object sendGate = new object();
    readonly Queue<Outgoing> sendFresh = new Queue<Outgoing>();
    readonly Queue<Outgoing> sendBulk = new Queue<Outgoing>();
    int droppedSends, sentPlays, sendSuccesses, sendFailures;
    string sendError;
    UdpClient socket;
    Thread receiver, sender;
    AutoResetEvent sendSignal;
    volatile bool running, receiverExited;
    string receiveError;

    // ---- 状態(テストと運営用) ----
    public bool NetworkRunning => socket != null;
    public int LocalPort { get; private set; }
    public string MachineId => machineId;
    public string Station => station;
    public int Count => log.Count;
    public string Hash => log.HashText;
    // 同期のメモリの記録(この台と相手の台の回、足した順。演奏中に届いた回も入る)の写し。書き換えても同期の記録は変わらず、
    // 一覧も書き換えられない。読むたびに写すので、1回の結果で1回くらいに。メインスレッドで。
    public IReadOnlyList<RankingPlay> LoggedPlays => log.CopyPlays();
    // 本番の同期(起動時に作った1つ)の LoggedPlays。同期が動いていなければ(テスト・Editor の既定・-rankingSync off)空。
    public static IReadOnlyList<RankingPlay> KnownPlays
    {
        get
        {
            var sync = Instance;
            if (sync == null || (sync.autoStarted && !AutoStartEnabled)) return Array.Empty<RankingPlay>();
            // 記録から外した回(RankingRemovals)は実績の条件に渡さない(はじめての人・一番乗りを、次の来場者が取れるように)。
            return RankingRemovals.Without(sync.LoggedPlays);
        }
    }

    // AchievementStore.CountSince から: メモリの記録のうち、その名前空間で sinceUnixMs 以後に遊び終わり、achId を取った回の ID を runs に足す
    // (写さずに読む)。
    internal void AddRunsWithAchievement(string ns, string achId, long sinceUnixMs, HashSet<string> runs)
    {
        if (autoStarted && !AutoStartEnabled) return;
        foreach (var play in log.Plays)
            if ((play.ns ?? "") == ns && play.at >= sinceUnixMs && play.ach != null && play.ach.Contains(achId) && !RankingRemovals.IsRemoved(play))
                runs.Add(play.id);
    }
    // 届いている相手の数(60秒以内に届いた相手)と、そのうち双方向と分かった相手の数。
    public int PeerCount
    {
        get
        {
            int count = 0;
            foreach (var peer in peers.Values) if (!peer.Lost) count++;
            return count;
        }
    }
    public int ConnectedCount
    {
        get
        {
            int count = 0;
            foreach (var peer in peers.Values) if (!peer.Lost && peer.Connected) count++;
            return count;
        }
    }
    // 届いている相手の台名。
    public string[] PeerStations
    {
        get
        {
            var list = new List<string>();
            foreach (var peer in peers.Values) if (!peer.Lost) list.Add(peer.Station);
            return list.ToArray();
        }
    }
    // 診断共有用の IP の写し。メインスレッドで読む。同期の状態や宛先ポートは変更しない。
    public IReadOnlyList<IPAddress> PeerAddresses
    {
        get
        {
            var addresses = new List<IPAddress>();
            foreach (var endpoint in manualPeers)
                if (!addresses.Contains(endpoint.Address)) addresses.Add(endpoint.Address);
            foreach (var peer in peers.Values)
                if (!peer.Lost && peer.EndPoint != null && !addresses.Contains(peer.EndPoint.Address)) addresses.Add(peer.EndPoint.Address);
            return addresses.AsReadOnly();
        }
    }
    // 最後に相手から届いた時刻(Time.realtimeSinceStartupAsDouble。まだなら -∞)。
    public double LastPeerMessageAt { get; private set; } = double.NegativeInfinity;
    // 相手から受け取って新しく足した回の数と、送った回の数(送り直し・宛先ごとも数える)。全部の記録を送り始めた回数。
    public int ReceivedPlays { get; private set; }
    // 動き出すときに、この台の表からログへ足した回の数(同期していない間に遊んだ回)。
    public int BackfilledPlays { get; private set; }
    public int SentPlays => Volatile.Read(ref sentPlays);
    public int BundlesStarted { get; private set; }
    // 表へ足す待ちの回の数と、表に書けずに足し直しを待っている回の数。同じ id で中身の違う回が届いた数。
    public int QueuedCount => freshApply.Count + bulkApply.Count + reconcileApply.Count;
    public int PendingCount => pending.Count;
    public int ConflictingPlays { get; private set; }
    // 書きかけ(ログ・足し直し待ち・実績の記録・ディスクへの保存)が残っているか。
    public bool SaveWaiting => logDirty || pendingStoreDirty || saveDirty || AchievementStore.NeedsWrite;
    public int TransfersWaiting => transfers.Count;
    public string LastNotice { get; private set; }
    public int NoticeCount { get; private set; }
    public int DroppedMessages { get { lock (gate) return droppedMessages; } }
    // 保存の失敗を起こす(テスト用)。FailSavesForTest はログ・足し直し待ちの書き出しとディスクへの保存、
    // FailLogWritesForTest はログの書き出しだけ、FailDiskSavesForTest はディスクへの保存(PlayerPrefs.Save)だけを失敗させる。
    public bool FailSavesForTest, FailLogWritesForTest, FailDiskSavesForTest;
    // 曲の演奏中かを決める(テスト用。null なら Game シーンにいるか)。
    public bool? BusyForTest;

    // 曲の演奏中か(Game シーンにいる間。判定調整・練習も含む)。束の併合・全部の記録の送信・ディスクへの保存はこの間止め、
    // 結果・選曲・タイトルの間にまとめて行う。新しい1回だけは演奏中でもすぐ足す(1回分なので軽い)。
    public bool Busy
    {
        get
        {
            if (BusyForTest.HasValue) return BusyForTest.Value;
            if (!Application.isPlaying) return false;
            var scene = SceneManager.GetActiveScene();
            if (busySceneHandle != scene.handle)
            {
                busySceneHandle = scene.handle;
                busyScene = scene.name == GameSceneName;
            }
            return busyScene;
        }
    }

    public static string ConnectedNotice(string peerStation) => "ランキング同期: " + Label(peerStation) + "とつながりました";
    public static string LostNotice(string peerStation) => "ランキング同期: " + Label(peerStation) + "と途切れています(記録はあとで合わせます)";
    public static string OneWayNotice(string peerStation) =>
        "ランキング同期: " + Label(peerStation) + "からは届いていますが、こちらから届いていません(ファイアウォール)";
    public const string NoPeerNotice =
        "ランキング同期: 相手の台が見つかりません(同じネットワークか・Windows はファイアウォールで UDP 5017 の受信を許可しているか・-rankingPeer)";
    public static string BindFailedNotice(int port) => "ランキング同期: UDP " + port + " を開けません(同期しません。ゲームはそのまま遊べます)";
    public const string SendFailedNotice = "ランキング同期: 送れません(ネットワークを確かめてください。ゲームはそのまま遊べます)";
    public static readonly string NearFullNotice =
        "ランキング同期: 同期の記録が " + RankingSyncLog.WarnPlays + " 回を超えました(" + RankingSyncLog.MaxPlays + " 回で同期を止めます)";
    public const string OldPeerNotice = "ランキング同期: 相手の台の版が古いので同期しません";
    public const string NewerPeerNotice = "ランキング同期: 相手の台の版が新しいので同期しません(この台を新しい版にしてください)";
    static string Label(string peerStation) => string.IsNullOrEmpty(peerStation) ? "もう1台" : "台" + peerStation;

    // ---- 作る ----

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Instance = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Initialize()
    {
        // テストかどうかは、作る前(Configure より前)に決める。ここで作らなければ、PlayerPrefs にもポートにも触れない。
        if (!AutoStartEnabled || Instance != null) return;
        string[] args = Environment.GetCommandLineArgs();
#if UNITY_EDITOR
        bool optIn = UnityEditor.EditorPrefs.GetBool(EditorEnabledKey, false);
        if (!EnabledHere(args, true, optIn)) return;
#else
        if (!EnabledHere(args, false, false)) return;
#endif
        if (GameObject.Find(TestRunnerObjectName) != null) return;
        var go = new GameObject("RankingSync");
        DontDestroyOnLoad(go);
        var sync = go.AddComponent<RankingSync>();
        sync.autoStarted = true;
        sync.Configure(new Settings
        {
            Peers = ResolvePeers(args, PlayerPrefs.GetString(PeersPreferenceKey, "")),
            Station = PhoneSaberStation.Read(),
            // 念のため少し待ってから動き出し(表の照合・ネットワーク)、その間に AutoStartEnabled が切られたら何もせずに消える(Update)。
            StartDelaySeconds = AutoStartDelaySeconds,
            Backfill = !Application.isEditor,
        });
        Instance = sync;
    }

    // テストと確認用に作る(Instance には入らない。DontDestroyOnLoad もしない)。
    public static RankingSync Create(Settings settings)
    {
        settings = settings ?? new Settings();
        var go = new GameObject("RankingSync " + PhoneSaberStation.Normalize(settings.Station));
        var sync = go.AddComponent<RankingSync>();
        sync.Configure(settings);
        return sync;
    }

    // 設定を読むだけ(PlayerPrefs へは書かない)。機械 ID が無ければ作るが、保存は最初に実際に送る直前まで遅らせる。
    void Configure(Settings value)
    {
        settings = value;
        station = PhoneSaberStation.Normalize(value.Station);
        if (RankingSyncLog.IsValidId(value.MachineId)) machineId = value.MachineId;
        else
        {
            machineId = PlayerPrefs.GetString(MachinePreferenceKey, "");
            if (!RankingSyncLog.IsValidId(machineId))
            {
                machineId = Guid.NewGuid().ToString("N");
                machineIdUnsaved = true;
            }
        }
        selfMarker = "\"from\":\"" + machineId + "\"";
        log = RankingSyncLog.Load(value.LogKey);
        savedPending = RankingSyncLog.Load(value.PendingKey);
        manualPeers.Clear();
        manualPeers.AddRange(RankingSyncLog.ParsePeers(value.Peers, value.PeerPort));
        double now = Time.realtimeSinceStartupAsDouble;
        startAt = now + Math.Max(0, value.StartDelaySeconds);
        nextBindAt = double.NegativeInfinity;
        nextSaveAt = double.NegativeInfinity;
        configured = true;
        if (value.StartDelaySeconds <= 0) Activate(now);
    }

    // 動き出す: 前回の足し直し待ち(相手の回)と、起動時の照合(ログの各回が表にあるか)を待ちに入れ、ネットワークを開く。
    void Activate(double now)
    {
        activated = true;
        activatedAt = listeningSince = now;
        foreach (var play in savedPending.Plays)
        {
            // もうログにある回(前回、ログを書いたあと別のキーを書く前に止まった)は、別のキーから消すだけ。
            if (log.Contains(play.id) || waiting.ContainsKey(play.id)) { pendingStoreDirty = true; continue; }
            var item = NewItem(play, true, true);
            item.Persist = true;
            waiting.Add(play.id, item);
            Put(bulkApply, item, ApplyWhere.Bulk);
        }
        foreach (var play in log.Plays)
        {
            if (waiting.ContainsKey(play.id)) continue;
            var item = NewItem(play, true, true);
            waiting.Add(play.id, item);
            Put(reconcileApply, item, ApplyWhere.Reconcile);
        }
        if (settings.Backfill) BackfillLocalPlays();
        // 起動の始めに、記録から外す回を表と実績から除いたとき(RankingRemovals)は、運営の記録に残す。
        if (RankingRemovals.CleanedAtStartup > 0) LogEvent("ranking-removals", "cleaned=" + RankingRemovals.CleanedAtStartup);
        // 実績の記録はここで読んでおく(曲の演奏中に届いた1回で、初めて読むことにならないように)。照合で、ログの回の実績も数に戻す。
        _ = AchievementStore.Available;
        CheckNearFull();
        if (!settings.Offline) OpenNetwork(now);
    }

    // この台の通算の表にあってログに無い回(同期が入る前の版・-rankingSync off で遊んだ回)を、この台の回としてログの後ろに足す
    // (RankingSyncBackfill)。表にはもうあるので表へは足さない。ログが変わると要約が変わり、相手の台へは全部の記録の束で届く。
    void BackfillLocalPlays()
    {
        List<RankingPlay> plays;
        try { plays = RankingSyncBackfill.Collect(settings.BackfillCharts ?? RankingSyncBackfill.StationCharts(), log.Find, waiting.ContainsKey, station); }
        catch (Exception exception)
        {
            // 調べられなくても、ほかの同期は今までどおり続ける。
            Debug.LogWarning("RankingSync: 同期していない間の回を調べられませんでした。" + exception.GetType().Name + ": " + exception.Message);
            return;
        }
        int added = 0;
        foreach (var play in plays)
        {
            if (log.IsFull) { WarnFull(); break; }
            if (log.Add(play)) added++;
        }
        if (added == 0) return;
        BackfilledPlays += added;
        logDirty = true;
        LogEvent("ranking-sync-backfill", "plays=" + added + " total=" + log.Count);
    }

    static ApplyItem NewItem(RankingPlay play, bool daily, bool high) =>
        new ApplyItem { Play = play, Chart = RankingSyncLog.ChartKey(play), Daily = daily, High = high };

    // 待ちに入れる(入れ直すたびに番号を増やし、前に入れた古い入れ物は取り出すときに飛ばす)。
    static void Put(Queue<Queued> queue, ApplyItem item, ApplyWhere where)
    {
        item.Ticket++;
        item.Where = where;
        queue.Enqueue(new Queued { Item = item, Ticket = item.Ticket });
    }

    // 待ちから1つ取り出す(入れ直された古い入れ物は飛ばす)。
    static bool TryTake(Queue<Queued> queue, out ApplyItem item)
    {
        while (queue.Count > 0)
        {
            var entry = queue.Dequeue();
            if (entry.Ticket != entry.Item.Ticket) continue;
            item = entry.Item;
            return true;
        }
        item = null;
        return false;
    }

    // 待ちから、ある曲×難易度の回だけを取り出す(ほかの回は順のまま残す。古い入れ物は捨てる)。
    static void TakeChart(Queue<Queued> queue, string chart, List<ApplyItem> batch)
    {
        int count = queue.Count;
        for (int i = 0; i < count; i++)
        {
            var entry = queue.Dequeue();
            if (entry.Ticket != entry.Item.Ticket) continue;
            if (entry.Item.Chart == chart) batch.Add(entry.Item);
            else queue.Enqueue(entry);
        }
    }

    // この起動で同期を動かすか(作る前に決める)。テストを回す起動(-runTests)・-rankingSync off・試験の起動では動かさない。
    // Editor では、開発中のプレイが本番の台へ混ざらないよう、メニュー(Tools > Ranking Sync > Editor でも同期する)で入れたときだけ。
    public static bool EnabledHere(string[] args, bool editor, bool editorOptIn)
    {
        if (args != null)
            foreach (string arg in args)
                if (arg == TestRunArgument || Array.IndexOf(DiagnosticArguments, arg) >= 0) return false;
        string value = ArgumentValue(args, SwitchArgument)?.ToLowerInvariant();
        if (value == "off" || value == "0") return false;
        return !editor || editorOptIn;
    }

    // 手で入れた相手: PlayerPrefs の値と、起動引数 -rankingPeer <IP>(何回でも)をつなげる。
    public static string ResolvePeers(string[] args, string preference)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(preference)) parts.Add(preference.Trim());
        if (args != null)
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == PeerArgument && !args[i + 1].StartsWith("-", StringComparison.Ordinal)) parts.Add(args[i + 1]);
        return string.Join(",", parts);
    }

    static string ArgumentValue(string[] args, string name)
    {
        if (args == null) return null;
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == name && !args[i + 1].StartsWith("-", StringComparison.Ordinal)) return args[i + 1];
        return null;
    }

#if UNITY_EDITOR
    [UnityEditor.MenuItem(EditorMenu)]
    static void ToggleEditor()
    {
        bool value = !UnityEditor.EditorPrefs.GetBool(EditorEnabledKey, false);
        UnityEditor.EditorPrefs.SetBool(EditorEnabledKey, value);
        Debug.Log("RankingSync: Editor での同期を" + (value ? "入れました(次の Play から。Editor で遊んだ回も相手の台へ送られます)" : "切りました(次の Play から)"));
    }

    [UnityEditor.MenuItem(EditorMenu, true)]
    static bool ToggleEditorCheck()
    {
        UnityEditor.Menu.SetChecked(EditorMenu, UnityEditor.EditorPrefs.GetBool(EditorEnabledKey, false));
        return true;
    }
#endif

    // ---- 自分の台で遊び終わった回 ----

    // GameSession.RecordCompletedDailyRanking から、この回につき1回だけ。dailyStored は、通算の表に書けたか
    // (書けなかったら、動いている間に足し直す)。同期が無いとき(テスト・Editor の既定・-rankingSync off)は何もしない
    // (ログにも残さない: テストや開発中の回を、あとで本番の台へ流さないため)。同期の失敗でゲームを止めない。
    public static bool RecordLocal(RankingPlay play, bool dailyStored = true)
    {
        try
        {
            LocalPlayRecorded?.Invoke(play);
            var sync = Instance;
            if (sync == null || (sync.autoStarted && !AutoStartEnabled)) return false;
            return sync.Record(play, dailyStored);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("RankingSync: この回を同期できませんでした。" + exception.GetType().Name + ": " + exception.Message);
            return false;
        }
    }

    // 結果画面で、自分の回を上位5件の表に書けなかった(表が壊れている・保存の失敗)とき(GameSession.RecordCompletedHighScore から)。
    // 表が直れば、動いている間に足し直す(同じ runId は二重にしない)。上位5件の足し直しは、この知らせがあった回だけ
    // (結果画面より先に足すと、NEW RECORD の比べる相手が変わるため)。
    public static void LocalHighScoreFailed(string runId)
    {
        try
        {
            var sync = Instance;
            if (sync == null || (sync.autoStarted && !AutoStartEnabled)) return;
            sync.MarkHighScorePending(runId);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("RankingSync: 上位5件の足し直しを予約できませんでした。" + exception.GetType().Name);
        }
    }

    // 曲が終わって通算の順位を決める直前(GameSession.RecordCompletedDailyRanking から)。その名前空間×曲×難易度で、表へ足す待ち・束の待ち・
    // 足し直し待ちにある回を、演奏中でも先に表へ足す(その1曲ぶんだけなので軽い)。結果画面の順位が、届いていた記録を入れた表で決まるように。
    public static void ApplyWaitingFor(string ns, string song, string difficulty)
    {
        try
        {
            var sync = Instance;
            if (sync == null || (sync.autoStarted && !AutoStartEnabled) || string.IsNullOrWhiteSpace(song)) return;
            sync.ApplyChart(ns, song, difficulty, Time.realtimeSinceStartupAsDouble);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("RankingSync: 届いていた記録を先に足せませんでした。" + exception.GetType().Name + ": " + exception.Message);
        }
    }

    // ある名前空間×曲×難易度の待ちの回を、いますぐ表へ足す(ApplyWaitingFor。テストからも呼ぶ)。新しくログに足した回の数を返す。
    public int ApplyChart(string ns, string song, string difficulty, double now)
    {
        if (!configured) return 0;
        string chart = RankingSyncLog.ChartKey(ns, song, difficulty);
        var batch = new List<ApplyItem>();
        TakeChart(freshApply, chart, batch);
        TakeChart(bulkApply, chart, batch);
        TakeChart(reconcileApply, chart, batch);
        for (int i = 0; i < pending.Count;)
        {
            if (pending[i].Chart != chart) { i++; continue; }
            batch.Add(pending[i]);
            pending.RemoveAt(i);
        }
        return batch.Count == 0 ? 0 : ApplyBatch(batch, now);
    }

    // この台で遊んだ回をログに残して、すぐ送る(0・0.3・1.0 秒の3回)。表(通算・上位5件)へは書かない(GameSession が残す)。
    // dailyStored が false(通算の表に書けなかった)なら、通算の表だけを足し直し待ちにする。
    public bool Record(RankingPlay play, bool dailyStored = true)
    {
        if (!configured) return false;
        if (!RankingSyncLog.IsValid(play))
        {
            if (!invalidWarned) { invalidWarned = true; Debug.LogWarning("RankingSync: 記録の形が正しくないので、この回は同期しません。"); }
            return false;
        }
        var known = log.Find(play.id);
        if (known != null)
        {
            if (!RankingSyncLog.SameContent(known, play)) WarnConflict();
            return false;
        }
        if (log.IsFull) { WarnFull(); return false; }
        log.Add(play);
        CheckNearFull();
        double now = Time.realtimeSinceStartupAsDouble;
        if (!dailyStored) AddPending(log.Find(play.id), true, false, now);
        // このあとすぐアプリを閉じても次の起動で相手と合わせられるよう、ログはすぐ書く(失敗したら Tick で書き直す)。
        logDirty = true;
        TryWriteLog();
        // 終わりの演出の間にアプリを閉じたとき(GamePlayManager.OnApplicationQuit)は、こちらが先に止まっていることがある。
        // ログに残して保存だけする(送れなかった分は、次の起動で要約の違いから合わせる)。
        if (stopped)
        {
            TrySavePrefs();
            return true;
        }
        var datagrams = log.Bundles(machineId, station, log.Count - 1);
        if (datagrams.Count == 0) return true;
        string text = datagrams[0].Text;
        SendToAll(text, 1);
        for (int i = 1; i < SendDelays.Length; i++) pendingSends.Add(new PendingSend { Due = now + SendDelays[i], Text = text });
        return true;
    }

    // 自分の回の上位5件だけを足し直し待ちにする(LocalHighScoreFailed。テストからも呼ぶ)。
    public void MarkHighScorePending(string runId)
    {
        var play = log.Find(runId);
        if (play != null) AddPending(play, false, true, Time.realtimeSinceStartupAsDouble);
    }

    // 足し直し待ちに入れる(もう待ちにある回なら、足す表を足すだけ)。
    void AddPending(RankingPlay play, bool daily, bool high, double now)
    {
        if (!waiting.TryGetValue(play.id, out var item))
        {
            item = NewItem(play, false, false);
            item.Where = ApplyWhere.Pending;
            waiting.Add(play.id, item);
            pending.Add(item);
        }
        item.Daily |= daily;
        item.High |= high;
        if (pendingRetryAt == double.NegativeInfinity) pendingRetryAt = now + PendingRetrySeconds;
    }

    // 手で入れる相手を足す(「IP」か「IP:ポート」)。開いていれば、その相手へすぐ要約を送る。足した数を返す。
    public int AddPeer(string text)
    {
        int added = 0;
        foreach (var endPoint in RankingSyncLog.ParsePeers(text, settings.PeerPort))
        {
            if (manualPeers.Exists(e => e.Equals(endPoint))) continue;
            manualPeers.Add(endPoint);
            added++;
            Enqueue(Encoding.UTF8.GetBytes(DigestMessage()), endPoint, 0);
        }
        return added;
    }

    // ---- 毎コマ ----

    void Update()
    {
        // 自動で作ったあとにテストが始まった(SetUpFixture が AutoStartEnabled を切った)。テストの回を送らないよう、すぐ消える。
        if (autoStarted && !AutoStartEnabled) { Destroy(gameObject); return; }
        Tick(Time.realtimeSinceStartupAsDouble);
    }

    // 1コマぶんの仕事。テストからは時刻を渡して呼ぶ。
    public void Tick(double now)
    {
        if (!configured || stopped) return;
        if (!activated)
        {
            if (now < startAt) return;
            Activate(now);
        }
        bool busy = Busy;
        // 受信が止まったときの立て直し(スレッドの Join をメインスレッドで待つ)は、演奏中は待たずに、明けてからにする。
        if (socket != null && receiverExited && !busy) { CloseNetwork(); nextBindAt = now + ReceiverRestartSeconds; }
        if (socket == null && !settings.Offline && now >= nextBindAt) OpenNetwork(now);
        FlushThreadNotes(now);
        FlushBroadcastNotes();
        DrainInbox(now, busy);
        if (!busy) RetryPending(now);
        ApplyQueued(now, busy);
        if (socket != null)
        {
            if (settings.Broadcast && now >= nextBroadcastRefreshAt) RefreshBroadcastsInBackground(now);
            if (now >= nextDigestAt)
            {
                nextDigestAt = now + DigestSeconds;
                SendToAll(DigestMessage(), 0);
            }
            SendDue(now);
            if (!busy) PumpTransfers();
        }
        CheckPeers(now);
        if (!busy) FlushSaves(now);
    }

    // ポートを開けない・開けた、にする(テスト用。開けたなら、見つからない知らせの30秒をそこから数える)。
    public void BindStateForTest(bool failing, double now)
    {
        bindFailing = failing;
        if (!failing) listeningSince = now;
    }

    // 受け取った1通を、受信スレッドを通さずに処理し、表へ足すところまで進める(テスト用。演奏中なら新しい1回だけ)。
    // 新しくログに足した回の数を返す。
    public int ReceiveForTest(string text, IPEndPoint from, double now)
    {
        Handle(text, from, now, RankingSyncLog.IsFresh(text));
        int added = 0;
        while (QueuedCount > 0)
        {
            int before = QueuedCount;
            added += ApplyQueued(now, Busy);
            if (QueuedCount >= before) break;
        }
        return added;
    }

    void DrainInbox(double now, bool busy)
    {
        // 新しい1回と要約は、演奏中でも先に。束は、演奏中と、表へ足す待ちが多いあいだは読み進めない。
        for (int i = 0; i < FreshPerFrame; i++)
        {
            Inbound item;
            lock (gate)
            {
                if (freshInbox.Count == 0) break;
                item = freshInbox.Dequeue();
            }
            SafeHandle(item, now, true);
        }
        if (busy) return;
        for (int i = 0; i < BulkPerFrame && bulkApply.Count < BulkBacklog; i++)
        {
            Inbound item;
            lock (gate)
            {
                if (bulkInbox.Count == 0) break;
                item = bulkInbox.Dequeue();
            }
            SafeHandle(item, now, false);
        }
    }

    void SafeHandle(Inbound item, double now, bool fresh)
    {
        try { Handle(item.Text, item.From, now, fresh); }
        catch (Exception exception)
        {
            // 1通の思わぬ失敗で、ほかの通やゲームを止めない。
            if (handleWarned) return;
            handleWarned = true;
            Debug.LogWarning("RankingSync: 届いた記録を処理できませんでした。" + exception.GetType().Name + ": " + exception.Message);
        }
    }

    // 受け取った1通の処理(メインスレッド)。新しい回は表へ足す待ちに入れる(足すのは ApplyQueued)。入れた回の数を返す。
    int Handle(string text, IPEndPoint from, double now, bool fresh)
    {
        // ほかの版の相手(ふだんは受信スレッドで分ける。ReceiveForTest はここを通る)。合流しない。
        if (RankingSyncLog.IsOtherVersion(text, out int otherVersion)) { NoticeOtherVersion(otherVersion); return 0; }
        if (from == null || !RankingSyncLog.TryParse(text, out var message)) return 0;
        // ブロードキャストは自分にも届く。自分の送ったものは捨てる。
        if (message.from == machineId) return 0;
        var peer = Touch(message, from, now);
        if (peer == null) return 0;
        if (message.kind == RankingSyncLog.DigestKind)
        {
            // 相手と記録が違えば、全部の記録を送る(同じ相手へは3秒に1回まで。送っている途中なら重ねない。演奏中は送るのを待つ)。
            if (!log.SameDigest(message.count, message.hash) && now >= peer.NextBundleAt && !transfers.Exists(t => t.Peer == peer))
            {
                peer.NextBundleAt = now + BundleSeconds;
                transfers.Add(new Transfer { Peer = peer });
                BundlesStarted++;
            }
            return 0;
        }
        int queued = 0;
        foreach (var play in message.plays)
        {
            if (!RankingSyncLog.IsValid(play)) continue;
            var logged = log.Find(play.id);
            if (logged != null)
            {
                if (!RankingSyncLog.SameContent(logged, play)) WarnConflict();
                continue;
            }
            if (waiting.TryGetValue(play.id, out var waitingItem))
            {
                if (!RankingSyncLog.SameContent(waitingItem.Play, play)) { WarnConflict(); continue; }
                // 束の待ちにある回へ、同じ回の新しい1回の知らせが届いた。新しい回の待ちへ移す
                // (二重には足さない。束の待ちに残った古い入れ物は、取り出すときに飛ばす)。
                if (fresh && waitingItem.Where == ApplyWhere.Bulk)
                {
                    Put(freshApply, waitingItem, ApplyWhere.Fresh);
                    queued++;
                }
                continue;
            }
            var item = NewItem(RankingSyncLog.Copy(play), true, true);
            waiting.Add(item.Play.id, item);
            if (fresh) Put(freshApply, item, ApplyWhere.Fresh);
            else Put(bulkApply, item, ApplyWhere.Bulk);
            queued++;
        }
        return queued;
    }

    // 表へ足す待ちの回を、新しい1回を先に、1コマ ApplyPerFrame 回まで足す(演奏中は新しい1回だけ)。
    // 曲×難易度ごとにまとめ、表の読み書きはそれぞれ1回。どちらかの表を書き換えたら、まず保存待ちを立てる。
    // 足せなかった表だけを足し直し待ちに残す。相手の回は、両方の表に足せたときだけログへ入れる。新しくログに足した回の数を返す。
    int ApplyQueued(double now, bool busy)
    {
        if (QueuedCount == 0) return 0;
        var batch = new List<ApplyItem>();
        ApplyItem taken;
        while (batch.Count < ApplyPerFrame && TryTake(freshApply, out taken)) batch.Add(taken);
        if (!busy)
        {
            while (batch.Count < ApplyPerFrame && TryTake(bulkApply, out taken)) batch.Add(taken);
            while (batch.Count < ApplyPerFrame && TryTake(reconcileApply, out taken)) batch.Add(taken);
        }
        return batch.Count == 0 ? 0 : ApplyBatch(batch, now);
    }

    // 取り出した回を、曲×難易度ごとにまとめて表へ足す(ApplyQueued と、曲の終わりの ApplyChart で共通)。新しくログに足した回の数を返す。
    int ApplyBatch(List<ApplyItem> batch, double now)
    {
        var groups = new Dictionary<string, List<ApplyItem>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var item in batch)
        {
            // 記録から外した回(RankingRemovals)は、表へも実績の数へも足さない。ログには入れて(相手の台と要約を同じに保つ)、待ちから外す。
            if (RankingRemovals.IsRemoved(item.Play)) { SkipRemoved(item); continue; }
            if (!groups.TryGetValue(item.Chart, out var group)) { groups.Add(item.Chart, group = new List<ApplyItem>()); order.Add(item.Chart); }
            group.Add(item);
        }
        int added = 0;
        foreach (string key in order)
        {
            var group = groups[key];
            var dailyResults = new RankingMergeResult?[group.Count];
            var highResults = new RankingMergeResult?[group.Count];
            MergeDaily(group, dailyResults);
            MergeHigh(group, highResults);
            // どちらか一方の表でも書き換えたら、ここで保存待ちを立てる(あとで失敗分を待ちへ回しても、書けた分を保存する)。
            for (int i = 0; i < group.Count; i++)
                if (dailyResults[i] == RankingMergeResult.Added || highResults[i] == RankingMergeResult.Added) saveDirty = true;
            for (int i = 0; i < group.Count; i++)
            {
                var item = group[i];
                // その回の実績を数に足す(表とは別。同じ回の同じ実績は二重に数えない。自分の台の回は解除したときに入っている)。
                // PlayerPrefs へは FlushSaves がまとめて書く。
                AchievementStore.Merge(item.Play.ns, item.Play.id, item.Play.ach, item.Play.st, item.Play.at);
                bool dailyFailed = dailyResults[i] == RankingMergeResult.Failed;
                bool highFailed = highResults[i] == RankingMergeResult.Failed;
                if (dailyFailed || highFailed)
                {
                    item.Daily = dailyFailed;
                    item.High = highFailed;
                    item.Where = ApplyWhere.Pending;
                    pending.Add(item);
                    if (pendingRetryAt == double.NegativeInfinity) pendingRetryAt = now + PendingRetrySeconds;
                    // 相手の回(ログにまだ無い)は、終了しても失われないよう別のキーにも残す。
                    if (!item.Persist && !log.Contains(item.Play.id)) { item.Persist = true; pendingStoreDirty = true; }
                    WarnApplyFailed(item.Play, dailyFailed);
                    continue;
                }
                item.Daily = item.High = false;
                item.Where = ApplyWhere.None;
                waiting.Remove(item.Play.id);
                // 別のキーに残していた回は、ログの保存に成功するまで、別のキーからは消さない(TryWriteLog が成功したら消す)。
                if (item.Persist) releasedPending.Add(item.Play);
                // 自分の回・起動時の照合の回(もうログにある)は足さない。
                if (log.Contains(item.Play.id)) continue;
                if (!log.Add(item.Play)) { WarnFull(); continue; }
                added++;
                logDirty = true;
            }
        }
        if (added > 0)
        {
            ReceivedPlays += added;
            CheckNearFull();
            LogEvent("ranking-sync-merge", "plays=" + added + " total=" + log.Count + " pending=" + pending.Count);
        }
        return added;
    }

    // 記録から外した回を、表へ足さずに待ちから外す。ログに無ければログにだけ入れる。別のキーの足し直し待ちからも消す
    // (ログに今入れた回は、ログの保存に成功してから)。
    void SkipRemoved(ApplyItem item)
    {
        item.Daily = item.High = false;
        item.Where = ApplyWhere.None;
        waiting.Remove(item.Play.id);
        if (!log.Contains(item.Play.id) && log.Add(item.Play))
        {
            logDirty = true;
            if (item.Persist) releasedPending.Add(item.Play);
        }
        else if (item.Persist) pendingStoreDirty = true;
    }

    // まとまり(同じ名前空間×曲×難易度)のうち、通算の表へ足す回を1回の読み書きで足す。結果は足さない回は null。
    static void MergeDaily(List<ApplyItem> group, RankingMergeResult?[] results)
    {
        var entries = new List<DailyRankingStore.Entry>();
        var index = new List<int>();
        long earliest = long.MaxValue;
        for (int i = 0; i < group.Count; i++)
        {
            if (!group[i].Daily) continue;
            var play = group[i].Play;
            entries.Add(new DailyRankingStore.Entry { runId = play.id, score = play.score });
            index.Add(i);
            earliest = Math.Min(earliest, play.at);
        }
        if (entries.Count == 0) return;
        var play0 = group[index[0]].Play;
        var merged = DailyRankingStore.MergeAll(play0.ns ?? "", play0.song, play0.diff, entries,
            DateTimeOffset.FromUnixTimeMilliseconds(earliest).LocalDateTime);
        for (int j = 0; j < index.Count; j++) results[index[j]] = merged[j];
    }

    // まとまり(同じ名前空間×曲×難易度)のうち、上位5件の表へ足す回を1回の読み書きで足す。結果は足さない回は null。
    static void MergeHigh(List<ApplyItem> group, RankingMergeResult?[] results)
    {
        var entries = new List<HighScoreEntry>();
        var index = new List<int>();
        for (int i = 0; i < group.Count; i++)
        {
            if (!group[i].High) continue;
            entries.Add(RankingSyncLog.ToHighScoreEntry(group[i].Play));
            index.Add(i);
        }
        if (entries.Count == 0) return;
        var play0 = group[index[0]].Play;
        var merged = HighScoreStore.MergeAll(play0.ns ?? "", play0.song, play0.diff, entries);
        for (int j = 0; j < index.Count; j++) results[index[j]] = merged[j];
    }

    // 表に書けなかった回を、しばらくしてから足し直す(表が直っていれば足せる)。演奏中は呼ばない。
    void RetryPending(double now)
    {
        if (pending.Count == 0 || now < pendingRetryAt) return;
        pendingRetryAt = double.NegativeInfinity;
        foreach (var item in pending) Put(bulkApply, item, ApplyWhere.Bulk);
        pending.Clear();
    }

    // 相手の状態を更新する。初めて・途切れたあとの1通では、こちらが聞こえていることをすぐ相手へ知らせる(双方向の確かめを早める)。
    Peer Touch(RankingSyncMessage message, IPEndPoint from, double now)
    {
        if (!peers.TryGetValue(message.from, out var peer))
        {
            if (peers.Count >= MaxPeers) return null;
            peer = new Peer { MachineId = message.from };
            peers.Add(message.from, peer);
        }
        peer.Station = message.st ?? "";
        peer.EndPoint = from;
        bool started = peer.Lost;
        if (started)
        {
            peer.Lost = false;
            peer.Connected = false;
            peer.OneWayNoticed = false;
            peer.FirstHeardAt = now;
            everHeard = true;
            LogEvent("ranking-sync-peer", "peer=" + Label(peer.Station) + " from=" + from);
        }
        peer.LastHeardAt = now;
        LastPeerMessageAt = now;
        // 相手の要約にこちらの機械 ID があれば、相手もこちらを聞こえている(双方向)。
        if (message.kind == RankingSyncLog.DigestKind && message.heard.Contains(machineId))
        {
            peer.LastAckAt = now;
            if (!peer.Connected)
            {
                peer.Connected = true;
                Notice(ConnectedNotice(peer.Station));
                LogEvent("ranking-sync-connected", "peer=" + Label(peer.Station));
            }
        }
        if (started) Enqueue(Encoding.UTF8.GetBytes(DigestMessage()), from, 0);
        return peer;
    }

    void CheckPeers(double now)
    {
        foreach (var peer in peers.Values)
        {
            if (peer.Lost) continue;
            if (now - peer.LastHeardAt > LostSeconds)
            {
                peer.Lost = true;
                peer.Connected = false;
                Notice(LostNotice(peer.Station));
                LogEvent("ranking-sync-lost", "peer=" + Label(peer.Station));
                continue;
            }
            // 相手からは届くのに、相手がこちらを聞こえなくなった(相手の要約からこちらが消えた)。
            if (peer.Connected && now - peer.LastAckAt > LostSeconds)
            {
                peer.Connected = false;
                peer.OneWayNoticed = true;
                Notice(OneWayNotice(peer.Station));
                LogEvent("ranking-sync-one-way", "peer=" + Label(peer.Station));
                continue;
            }
            if (!peer.Connected && !peer.OneWayNoticed && now - peer.FirstHeardAt > OneWaySeconds)
            {
                peer.OneWayNoticed = true;
                Notice(OneWayNotice(peer.Station));
                LogEvent("ranking-sync-one-way", "peer=" + Label(peer.Station));
            }
        }
        // ポートを開けてから30秒たっても相手が一度も見つからない。今ポートを開けないあいだは、そちらの知らせだけにする
        // (あとで開けたら、そこから数え直す)。ほかの版の相手が聞こえていれば、その知らせだけにする。
        if (!everHeard && !noPeerNoticed && !bindFailing && !oldPeerNoticed && !newerPeerNoticed && now - listeningSince > NoPeerSeconds)
        {
            noPeerNoticed = true;
            Notice(NoPeerNotice);
            LogEvent("ranking-sync-no-peer", "seconds=" + NoPeerSeconds);
        }
    }

    // この台の要約。最近(60秒以内に)聞こえた相手の機械 ID を、新しい順に入れる。
    string DigestMessage()
    {
        var heard = new List<Peer>();
        foreach (var peer in peers.Values) if (!peer.Lost) heard.Add(peer);
        heard.Sort((a, b) => b.LastHeardAt.CompareTo(a.LastHeardAt));
        var ids = new List<string>(Math.Min(heard.Count, RankingSyncLog.MaxHeard));
        for (int i = 0; i < heard.Count && i < RankingSyncLog.MaxHeard; i++) ids.Add(heard[i].MachineId);
        return log.DigestMessage(machineId, station, ids);
    }

    void Notice(string message)
    {
        LastNotice = message;
        NoticeCount++;
        Debug.Log("RankingSync: " + message);
        if (settings.ShowNotices) OperatorNotice.Show(message, NoticeSeconds);
    }

    // 運営用の events.log(PhoneSaberEventLog)へ1行。本番の1つだけが書く(テストの回を運営の記録に混ぜない)。
    void LogEvent(string kind, string detail)
    {
        if (autoStarted) PhoneSaberEventLog.Record(station, "SYSTEM", kind, detail);
    }

    void WarnFull()
    {
        if (fullWarned) return;
        fullWarned = true;
        Debug.LogWarning("RankingSync: 同期の記録が " + RankingSyncLog.MaxPlays + " 回に達したので、ここから先の回は同期しません(この台の表には残ります)。");
    }

    void CheckNearFull()
    {
        if (nearFullNoticed || log.Count <= RankingSyncLog.WarnPlays) return;
        nearFullNoticed = true;
        Notice(NearFullNotice);
    }

    void WarnConflict()
    {
        ConflictingPlays++;
        if (conflictWarned) return;
        conflictWarned = true;
        Debug.LogWarning("RankingSync: 同じ回の ID で中身の違う記録が届きました。先に来たほうを残します。");
    }

    void WarnApplyFailed(RankingPlay play, bool daily)
    {
        if (!applyWarned.Add(RankingSyncLog.ChartKey(play))) return;
        Debug.LogWarning("RankingSync: " + (string.IsNullOrEmpty(play.ns) ? "" : "[" + play.ns + "] ") + play.song + "(" + play.diff + ")の" +
                         (daily ? "通算の順位" : "上位5件") + "の表を読み書きできないので、その回をあとで足し直します(壊れた表は上書きしません)。");
    }

    // ほかの版の相手が聞こえた(合流しない)。古い・新しいそれぞれ1回だけ知らせる。
    void NoticeOtherVersion(int version)
    {
        if (version < RankingSyncLog.Version)
        {
            if (oldPeerNoticed) return;
            oldPeerNoticed = true;
            Notice(OldPeerNotice);
        }
        else
        {
            if (newerPeerNoticed) return;
            newerPeerNoticed = true;
            Notice(NewerPeerNotice);
        }
        LogEvent("ranking-sync-version", "peer-version=" + version + " mine=" + RankingSyncLog.Version);
    }

    // ---- 保存 ----

    // ログを PlayerPrefs へ書く。成功したときだけ書きかけの印を下ろす(失敗したら Tick が少し待って書き直す)。
    // 成功したら、足せてログへ入れた回を、足し直し待ちの別のキーから消してよくなる(次の TryWritePending で消す)。
    bool TryWriteLog()
    {
        try
        {
            if (FailSavesForTest || FailLogWritesForTest) throw new InvalidOperationException("テスト用の保存失敗");
            log.Save(settings.LogKey);
            logDirty = false;
            saveDirty = true;
            if (releasedPending.Count > 0)
            {
                releasedPending.Clear();
                pendingStoreDirty = true;
            }
            return true;
        }
        catch (Exception exception)
        {
            WarnSave(exception);
            return false;
        }
    }

    // 足し直し待ちの相手の回(ログにまだ無い)を、別のキーへ書く(無くなったら消す)。足せてもログの保存がまだの回
    // (releasedPending)も残す(ログを書けないまま終わっても、ログにも別のキーにも残らない回を作らない)。
    bool TryWritePending()
    {
        try
        {
            if (FailSavesForTest) throw new InvalidOperationException("テスト用の保存失敗");
            var plays = new List<RankingPlay>(releasedPending);
            foreach (var item in waiting.Values) if (item.Persist) plays.Add(item.Play);
            if (plays.Count == 0) PlayerPrefs.DeleteKey(settings.PendingKey);
            else PlayerPrefs.SetString(settings.PendingKey, RankingSyncLog.PlaysJson(plays));
            pendingStoreDirty = false;
            saveDirty = true;
            return true;
        }
        catch (Exception exception)
        {
            WarnSave(exception);
            return false;
        }
    }

    bool TrySavePrefs()
    {
        try
        {
            if (FailSavesForTest || FailDiskSavesForTest) throw new InvalidOperationException("テスト用の保存失敗");
            PlayerPrefs.Save();
            saveDirty = false;
            return true;
        }
        catch (Exception exception)
        {
            WarnSave(exception);
            return false;
        }
    }

    void WarnSave(Exception exception)
    {
        if (saveWarned) return;
        saveWarned = true;
        Debug.LogWarning("RankingSync: 同期の記録を保存できませんでした(" + exception.GetType().Name + ")。少し待って保存し直します。");
    }

    // 相手の回の実績を足した記録(AchievementStore)を PlayerPrefs へ書く(ディスクへの保存は TrySavePrefs)。
    bool TryWriteAchievements()
    {
        if (FailSavesForTest) { WarnSave(new InvalidOperationException("テスト用の保存失敗")); return false; }
        if (!AchievementStore.WriteToPrefs()) return false;
        saveDirty = true;
        return true;
    }

    // ログ・足し直し待ち・実績の記録の書き出しとディスクへの保存は、まとめて 0.5 秒に1回まで(1コマに1回より少なく。演奏中は呼ばない)。
    // 失敗したら 2 秒待って書き直す。
    void FlushSaves(double now)
    {
        if (!SaveWaiting || now < nextSaveAt) return;
        nextSaveAt = now + SaveIntervalSeconds;
        if (logDirty && !TryWriteLog()) { nextSaveAt = now + SaveRetrySeconds; return; }
        if (pendingStoreDirty && !TryWritePending()) { nextSaveAt = now + SaveRetrySeconds; return; }
        if (AchievementStore.NeedsWrite && !TryWriteAchievements()) { nextSaveAt = now + SaveRetrySeconds; return; }
        if (saveDirty && !TrySavePrefs()) nextSaveAt = now + SaveRetrySeconds;
    }

    // ---- 送る(送信スレッドへ渡す) ----

    List<IPEndPoint> Targets()
    {
        var targets = new List<IPEndPoint>();
        if (settings.Broadcast)
            foreach (var address in Volatile.Read(ref scan).Addresses) AddTarget(targets, new IPEndPoint(address, settings.PeerPort));
        foreach (var endPoint in manualPeers) AddTarget(targets, endPoint);
        foreach (var peer in peers.Values) if (peer.EndPoint != null) AddTarget(targets, peer.EndPoint);
        return targets;
    }

    static void AddTarget(List<IPEndPoint> targets, IPEndPoint target)
    {
        foreach (var existing in targets) if (existing.Equals(target)) return;
        targets.Add(target);
    }

    void SendToAll(string text, int plays)
    {
        if (socket == null) return;
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        foreach (var target in Targets()) Enqueue(bytes, target, plays);
    }

    // 送る待ちに入れる(メインスレッド)。送るのは送信スレッド(メインスレッドは送信を待たない)。待ちが満杯なら捨てる。
    // bulk(全部の記録の束)は、新しい1回と要約のあとに送る。
    bool Enqueue(byte[] bytes, IPEndPoint target, int plays, bool bulk = false)
    {
        if (socket == null || target == null || !ReadyToSend()) return false;
        lock (sendGate)
        {
            var queue = bulk ? sendBulk : sendFresh;
            if (queue.Count >= SendQueueCapacity) { droppedSends++; return false; }
            queue.Enqueue(new Outgoing { Bytes = bytes, Target = target, Plays = plays });
        }
        sendSignal?.Set();
        return true;
    }

    int SendQueueCount { get { lock (sendGate) return sendFresh.Count + sendBulk.Count; } }
    int BulkSendCount { get { lock (sendGate) return sendBulk.Count; } }

    void SendDue(double now)
    {
        for (int i = 0; i < pendingSends.Count;)
        {
            if (pendingSends[i].Due > now) { i++; continue; }
            string text = pendingSends[i].Text;
            pendingSends.RemoveAt(i);
            SendToAll(text, 1);
        }
    }

    // 全部の記録を、相手ごとに少しずつ作って束の送る待ちへ入れる(束の送る待ちが少ないときだけ、1コマ SendsPerFrame 通まで。
    // 相手が複数なら順番に。演奏中は呼ばない)。送り終えたら自分の要約も、束のあとに返す(相手も、自分に足りない分をすぐ送り返せるように)。
    void PumpTransfers()
    {
        int budget = Math.Min(SendsPerFrame, TransferQueueRoom - BulkSendCount);
        while (budget > 0 && transfers.Count > 0)
        {
            var transfer = transfers[0];
            transfers.RemoveAt(0);
            var target = transfer.Peer.EndPoint;
            if (target == null) continue;
            var datagrams = log.Bundles(machineId, station, transfer.Next, Math.Min(budget, DatagramsPerTransferStep), out int next);
            foreach (var datagram in datagrams)
            {
                Enqueue(Encoding.UTF8.GetBytes(datagram.Text), target, datagram.Plays, true);
                budget--;
            }
            if (datagrams.Count == 0) budget--; // 必ず進む(同じコマで回り続けない)
            transfer.Next = next;
            if (next >= log.Count)
            {
                Enqueue(Encoding.UTF8.GetBytes(DigestMessage()), target, 0, true);
                budget--;
            }
            else transfers.Add(transfer);
        }
    }

    // 送る直前の確かめ(メインスレッド): テストが始まっていたら送らない。機械 ID を初めて作ったなら、ここで保存する(最初に実際に送る直前)。
    bool ReadyToSend()
    {
        if (autoStarted && !AutoStartEnabled) return false;
        if (!machineIdUnsaved) return true;
        machineIdUnsaved = false;
        try
        {
            PlayerPrefs.SetString(MachinePreferenceKey, machineId);
            PlayerPrefs.Save();
        }
        catch (Exception exception)
        {
            // 保存できなくても送るのは続ける(次の起動で機械 ID が変わり、相手には新しい台に見えるだけ)。
            if (!idWarned) { idWarned = true; Debug.LogWarning("RankingSync: 機械 ID を保存できませんでした。" + exception.GetType().Name); }
        }
        return true;
    }

    // 送信スレッド。送る待ちから1通ずつ送る(新しい1回と要約を、全部の記録の束より先に。UdpClient の送信は別スレッドで呼んでよい)。
    // 成功・失敗は数だけを残す。
    void SendLoop(UdpClient client, AutoResetEvent signal)
    {
        try
        {
            while (running && ReferenceEquals(Volatile.Read(ref socket), client))
            {
                Outgoing item = default;
                bool has;
                lock (sendGate)
                {
                    var queue = sendFresh.Count > 0 ? sendFresh : sendBulk;
                    has = queue.Count > 0;
                    if (has) item = queue.Dequeue();
                }
                if (!has)
                {
                    signal.WaitOne(500);
                    continue;
                }
                try
                {
                    client.Send(item.Bytes, item.Bytes.Length, item.Target);
                    Interlocked.Add(ref sentPlays, item.Plays);
                    Interlocked.Increment(ref sendSuccesses);
                }
                catch (Exception exception) when (exception is SocketException || exception is ObjectDisposedException ||
                                                  exception is InvalidOperationException)
                {
                    if (!running || !ReferenceEquals(Volatile.Read(ref socket), client)) return;
                    Interlocked.Increment(ref sendFailures);
                    Interlocked.CompareExchange(ref sendError, item.Target + " へ送れませんでした(" + Describe(exception) + ")", null);
                }
            }
        }
        catch (Exception)
        {
            // 止めるとき(合図を閉じた)の例外は普通のこと。
        }
    }

    // 受信・送信スレッドが残したことを、メインスレッドで1回だけ警告・知らせにする。
    void FlushThreadNotes(double now)
    {
        string error = Interlocked.Exchange(ref receiveError, null);
        if (error != null && !receiveWarned)
        {
            receiveWarned = true;
            Debug.LogWarning("RankingSync: 受信が止まりました(" + error + ")。開き直します。");
        }
        string sendNote = Interlocked.Exchange(ref sendError, null);
        if (sendNote != null && !sendWarned)
        {
            sendWarned = true;
            Debug.LogWarning("RankingSync: " + sendNote + "。届く相手には送り続けます。");
        }
        int otherVersion = Interlocked.Exchange(ref otherVersionHeard, 0);
        if (otherVersion != 0) NoticeOtherVersion(otherVersion);
        // 開いてから2回目の要約(各ネットワークへのブロードキャストを含む)を送り終えても、どこへも送れていない(ネットワークが無いなど)。
        // 運営に1回だけ知らせる。
        if (!sendNoticed && socket != null && now - listeningSince > DigestSeconds + 1 && Volatile.Read(ref sendFailures) > 0 &&
            Volatile.Read(ref sendSuccesses) == 0 && SendQueueCount == 0)
        {
            sendNoticed = true;
            Notice(SendFailedNotice);
        }
    }

    static string Describe(Exception exception) =>
        exception is SocketException socketException ? socketException.SocketErrorCode.ToString() : exception.GetType().Name;

    // ---- ネットワークを開く・閉じる ----

    void OpenNetwork(double now)
    {
        if (settings.Offline || socket != null) return;
        UdpClient client = null;
        try
        {
            client = new UdpClient(AddressFamily.InterNetwork);
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
            // ほかのアプリに同じポートを横取りされないよう、排他で開く(PhoneSaberDiscoveryResponder と同じ)。
            client.ExclusiveAddressUse = true;
            // 相手が閉じたあとの「届かない」(ICMP)で、次の受信が例外になるのを止める(SIO_UDP_CONNRESET)。
            try { client.Client.IOControl(unchecked((int)0x9800000C), new byte[4], null); }
            catch (Exception) { }
#endif
            client.EnableBroadcast = true;
            client.Client.Bind(new IPEndPoint(settings.LoopbackOnly ? IPAddress.Loopback : IPAddress.Any, settings.Port));
            client.Client.ReceiveTimeout = 500;
            try { client.Client.ReceiveBufferSize = 256 * 1024; }
            catch (Exception) { }
            LocalPort = ((IPEndPoint)client.Client.LocalEndPoint).Port;
        }
        catch (Exception exception)
        {
            try { client?.Close(); }
            catch (Exception) { }
            nextBindAt = now + BindRetrySeconds;
            // 「今ポートを開けない」(bindFailing)と「警告した」(bindWarned)は別。見つからない知らせは今の状態で決める。
            bindFailing = true;
            if (!bindWarned)
            {
                bindWarned = true;
                Debug.LogWarning("RankingSync: UDP " + settings.Port + " を開けないので、ランキングを同期しません(" + Describe(exception) +
                                 ")。" + BindRetrySeconds + " 秒ごとに開き直します。ゲームは今までどおり動きます。");
                Notice(BindFailedNotice(settings.Port));
                LogEvent("ranking-sync-failed", "port=" + settings.Port + " error=" + Describe(exception));
            }
            return;
        }
        string marker = selfMarker;
        var signal = new AutoResetEvent(false);
        running = true;
        receiverExited = false;
        Interlocked.Exchange(ref sendSuccesses, 0);
        Interlocked.Exchange(ref sendFailures, 0);
        sendSignal = signal;
        Volatile.Write(ref socket, client);
        receiver = new Thread(() => ReceiveLoop(client, marker)) { IsBackground = true, Name = "RankingSync receiver" };
        sender = new Thread(() => SendLoop(client, signal)) { IsBackground = true, Name = "RankingSync sender" };
        receiver.Start();
        sender.Start();
        if (bindFailing) Debug.Log("RankingSync: UDP " + LocalPort + " を開けたので、同期を始めます。");
        bindFailing = false;
        listeningSince = now;
        // ネットワークごとのブロードキャストを背景で調べ始める。すぐ1回、要約を送って相手を見つける
        // (調べ終わる前の1回目は 255.255.255.255 と手で入れた相手だけ。5秒後の2回目から各ネットワークへも)。
        if (settings.Broadcast) RefreshBroadcastsInBackground(now);
        nextDigestAt = now;
        LogEvent("ranking-sync-start", "port=" + LocalPort + " plays=" + log.Count + " peers=" + manualPeers.Count);
    }

    // 受信スレッド。届いた文字列のふるい分けと、2つのキュー(新しい1回と要約 / 束)への追加だけ(解析・PlayerPrefs はメインスレッド)。
    void ReceiveLoop(UdpClient client, string marker)
    {
        var buffer = new byte[ReceiveBufferBytes];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (running && ReferenceEquals(Volatile.Read(ref socket), client))
            {
                int length;
                try { length = client.Client.ReceiveFrom(buffer, ref remote); }
                catch (SocketException exception) when (IsTransient(exception.SocketErrorCode))
                {
                    continue;
                }
                // ほかの版の相手は合流しない(版だけ残し、メインスレッドが運営に1回知らせる)。
                if (RankingSyncLog.IsOtherVersion(buffer, length, out int otherVersion))
                {
                    Interlocked.CompareExchange(ref otherVersionHeard, otherVersion, 0);
                    continue;
                }
                // 大きすぎる・ほかの目印・壊れた UTF-8 はここで捨てる。自分の送ったもの(ブロードキャスト)もキューに入れない。
                if (!RankingSyncLog.TryDecode(buffer, length, out string text) ||
                    text.IndexOf(marker, StringComparison.Ordinal) >= 0 || !(remote is IPEndPoint from)) continue;
                bool fresh = RankingSyncLog.IsFresh(text);
                lock (gate)
                {
                    var queue = fresh ? freshInbox : bulkInbox;
                    if (queue.Count >= (fresh ? FreshInboxCapacity : BulkInboxCapacity)) droppedMessages++;
                    else queue.Enqueue(new Inbound { Text = text, From = new IPEndPoint(from.Address, from.Port) });
                }
            }
        }
        catch (Exception exception)
        {
            // 止めるとき(ソケットを閉じた)の例外は普通のこと。動いている間の例外だけを、メインスレッドで1回だけ知らせる。
            if (running && ReferenceEquals(Volatile.Read(ref socket), client))
                Interlocked.CompareExchange(ref receiveError, Describe(exception), null);
        }
        finally
        {
            // 開き直したあとの古いスレッドが、新しいソケットを止めたことにしない。
            if (ReferenceEquals(Volatile.Read(ref socket), client)) receiverExited = true;
        }
    }

    // 待ち時間切れ・大きすぎる1通・相手が閉じたあとの「届かない」(ICMP、Windows)など、受信を続けてよい失敗。
    static bool IsTransient(SocketError error) =>
        error == SocketError.TimedOut || error == SocketError.WouldBlock || error == SocketError.Interrupted ||
        error == SocketError.MessageSize || error == SocketError.ConnectionReset || error == SocketError.NetworkReset ||
        error == SocketError.HostUnreachable || error == SocketError.NetworkUnreachable;

    void CloseNetwork()
    {
        var client = socket;
        var receiveThread = receiver;
        var sendThread = sender;
        var signal = sendSignal;
        running = false;
        Volatile.Write(ref socket, null);
        receiver = sender = null;
        sendSignal = null;
        try { signal?.Set(); }
        catch (Exception) { }
        try { client?.Close(); }
        catch (Exception) { }
        bool joined = Join(receiveThread) & Join(sendThread);
        // スレッドが残っていたら、合図は閉じない(残ったスレッドが待っている)。
        if (joined) signal?.Dispose();
        lock (sendGate)
        {
            sendFresh.Clear();
            sendBulk.Clear();
        }
        receiverExited = false;
    }

    static bool Join(Thread thread)
    {
        if (thread == null || thread == Thread.CurrentThread || thread.Join(JoinMilliseconds)) return true;
        Debug.LogWarning("RankingSync: " + thread.Name + " が " + JoinMilliseconds + "ms で止まりませんでした。");
        return false;
    }

    // 動いている IPv4 のネットワークごとの宛先ブロードキャストと 255.255.255.255。マスクが分からないネットワークは /24 と推測し、
    // 読めないネットワークは飛ばす(どちらも名前を残し、メインスレッドが1回だけ警告する)。取れなければ 255.255.255.255 だけ。
    static BroadcastScan FindBroadcastAddresses()
    {
        var result = new BroadcastScan();
        var addresses = new List<IPAddress> { IPAddress.Broadcast };
        var guessed = new List<string>();
        var unreadable = new List<string>();
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (Exception exception)
        {
            result.Unreadable = "(" + exception.GetType().Name + ")";
            return result;
        }
        foreach (var networkInterface in interfaces)
        {
            string name = "?";
            try
            {
                name = networkInterface.Name;
                if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var status = networkInterface.OperationalStatus;
                if (status != OperationalStatus.Up && status != OperationalStatus.Unknown) continue;
                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    IPAddress mask = null;
                    try { mask = unicast.IPv4Mask; }
                    catch (Exception) { }
                    var broadcast = RankingSyncLog.DirectedBroadcast(unicast.Address, mask, out bool guess);
                    if (broadcast == null) continue;
                    if (guess) guessed.Add(name + " " + unicast.Address + " → " + broadcast);
                    if (!addresses.Contains(broadcast)) addresses.Add(broadcast);
                }
            }
            catch (Exception exception) { unreadable.Add(name + "(" + exception.GetType().Name + ")"); }
        }
        result.Addresses = addresses.ToArray();
        if (guessed.Count > 0) result.Guessed = string.Join("・", guessed);
        if (unreadable.Count > 0) result.Unreadable = string.Join("・", unreadable);
        return result;
    }

    void FlushBroadcastNotes()
    {
        var current = Volatile.Read(ref scan);
        if (current.Guessed != null && !guessWarned)
        {
            guessWarned = true;
            Debug.LogWarning("RankingSync: ネットワークのマスクが分からないので、" + current.Guessed +
                             " は推測した宛先です。255.255.255.255 と手で入れた相手にも送ります。届かなければ -rankingPeer <相手の IP> を使ってください。");
        }
        if (current.Unreadable != null && !unreadableWarned)
        {
            unreadableWarned = true;
            Debug.LogWarning("RankingSync: ネットワーク " + current.Unreadable +
                             " の情報を読めなかったので、そこへはブロードキャストしません(255.255.255.255 と手で入れた相手には送ります)。");
        }
    }

    // ネットワークの調べ直しは時間のかかることがある(Windows で 40ms ほど)ので、曲の途中で引っかからないよう背景で。
    void RefreshBroadcastsInBackground(double now)
    {
        nextBroadcastRefreshAt = now + BroadcastRefreshSeconds;
        if (Interlocked.CompareExchange(ref refreshingBroadcasts, 1, 0) != 0) return;
        try
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Volatile.Write(ref scan, FindBroadcastAddresses()); }
                catch (Exception) { }
                finally { Volatile.Write(ref refreshingBroadcasts, 0); }
            });
        }
        catch (Exception) { Volatile.Write(ref refreshingBroadcasts, 0); }
    }

    // ---- 止める ----

    // ネットワークを止め、書きかけの記録を保存する(消えるとき・終了・テストの後片付け)。このあとは開き直さない。
    public void Shutdown()
    {
        stopped = true;
        Flush();
    }

    // 終了するときも、ふだんの保存(FlushSaves)と同じ順番: ログ → 足し直し待ちの別のキー → ディスクへの保存。
    // ログを書けなかったときは、足せた回を別のキーから消さない(releasedPending が残るので、TryWritePending はその回も書く)。
    void Flush()
    {
        CloseNetwork();
        if (logDirty) TryWriteLog();
        if (pendingStoreDirty) TryWritePending();
        if (AchievementStore.NeedsWrite) TryWriteAchievements();
        if (saveDirty) TrySavePrefs();
    }

    // 無効にされたら止め、有効に戻れば次の Tick で開き直す。
    void OnDisable() => Flush();
    void OnApplicationQuit() => Shutdown();

    void OnDestroy()
    {
        Shutdown();
        if (Instance == this) Instance = null;
    }
}
