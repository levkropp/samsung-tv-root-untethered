// TvRootBridgeRelay: headless token-gated TCP relay (TV:port -> 127.0.0.1:26101).
//
// WHY THIS EXISTS (2026-10-10): the relay used to live inside the manager
// UI process, so closing the manager (or the platform reaping it when the
// TV switches inputs) killed management access along with it. This relay
// has no window and no app lifecycle: it runs supervised under systemd
// (tvroot-bridge.service, written by the telemetry module each boot),
// survives manager open/close, and restarts on failure. The manager keeps
// only a port probe for its status page. Relay needs no root - it starts
// whenever the module runs - but binds the same port/token/log format as
// the old in-agent relay, so all tooling is unchanged.
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public static class BridgeRelay
{
    private const int DefaultPort = 26103;
    private const string DefaultTokenPath = "/opt/usr/share/selfroot/bridge.conf";
    private const string DefaultLogPath = "/home/owner/share/tmp/sdk_tools/selfroot-evidence/bridge.log";
    private const int SdbPort = 26101;
    // Overridable so the relay can be exercised off a television; sdbd itself always listens on
    // loopback 26101, and nothing else about the bridge changes.
    private static int sdbPort = ReadSdbPort();
    private const int TokenLength = 32;

    // Half-open connections are what wedges sdbd: a client that vanishes without a FIN leaves both
    // pumps blocked in Read, so neither leg is ever closed and sdbd's single session stays pinned
    // until the television is rebooted. Keepalive answers that in about thirty seconds; the idle
    // watchdog is the backstop for a stack that ignores keepalive, and the grace period is for a
    // peer that never answers our FIN. An unused transport is never trusted with the next session.
    private const int KeepAliveIdleSeconds = 15;
    private const int KeepAliveIntervalSeconds = 5;
    private const int KeepAliveProbeCount = 3;
    private const int CloseGraceMs = 1000;
    private const int DefaultIdleTimeoutMs = 1800000;
    // Linux SOL_SOCKET / SO_KEEPALIVE / IPPROTO_TCP; the TCP_* values below use the same numbering.
    private const int SolSocket = 1;
    private const int SoKeepAlive = 9;
    private const int IpprotoTcp = 6;
    // Linux TCP_KEEPIDLE / TCP_KEEPINTVL / TCP_KEEPCNT; netcoreapp3.1 has no managed names for these.
    private const int TcpKeepIdle = 4;
    private const int TcpKeepInterval = 5;
    private const int TcpKeepCount = 6;
    private static int attachedSessions;
    private static long sessionCounter;
    private static int idleTimeoutMs = DefaultIdleTimeoutMs;

    private static string LogPath = DefaultLogPath;
    private static readonly object LogLock = new object();

    public static int Main(string[] args)
    {
        int port = DefaultPort;
        string tokenPath = DefaultTokenPath;
        if (args.Length > 0)
        {
            int parsed;
            if (!int.TryParse(args[0], out parsed) || parsed < 1024 || parsed > 65535)
            {
                Console.Error.WriteLine(
                    "usage: TvRootBridgeRelay [port] [tokenPath] [logPath] [idleTimeoutMs]");
                return 2;
            }
            port = parsed;
        }
        if (args.Length > 1) tokenPath = args[1];
        if (args.Length > 2) LogPath = args[2];
        if (args.Length > 3)
        {
            int idle;
            if (!int.TryParse(args[3], out idle) || idle < 5000 || idle > 86400000)
            {
                Console.Error.WriteLine("idleTimeoutMs must be between 5000 and 86400000");
                return 2;
            }
            idleTimeoutMs = idle;
        }

        string token;
        try { token = File.ReadAllText(tokenPath).Trim(); }
        catch (Exception e)
        {
            Console.Error.WriteLine("cannot read token: " + e.GetType().Name);
            return 2;
        }
        if (!IsToken(token))
        {
            Console.Error.WriteLine("token must be 32 lowercase hex characters");
            return 2;
        }

        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start(8);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("cannot listen on " + port + ": " + e.GetType().Name);
            return 3;
        }
        Log("relay listening on port " + port);
        Log("idle timeout " + idleTimeoutMs + "ms; keepalive " + KeepAliveIdleSeconds + "s idle, "
            + KeepAliveIntervalSeconds + "s x" + KeepAliveProbeCount + " probes; close grace "
            + CloseGraceMs + "ms; upstream 127.0.0.1:" + sdbPort);
        while (true)
        {
            try
            {
                var client = listener.AcceptTcpClient();
                Task.Run(() => HandleBridgeClient(client, token));
            }
            catch (Exception e)
            {
                Log("listener error: " + e.GetType().Name);
                Thread.Sleep(1000);
            }
        }
    }

    private static bool IsToken(string token)
    {
        if (token == null || token.Length != TokenLength) return false;
        foreach (var character in token)
        {
            if (!((character >= '0' && character <= '9')
                || (character >= 'a' && character <= 'f'))) return false;
        }
        return true;
    }

    private static void HandleBridgeClient(TcpClient client, string token)
    {
        var peer = "unknown";
        TcpClient upstream = null;
        var bridged = false;
        try
        {
            peer = client.Client.RemoteEndPoint == null
                ? peer
                : client.Client.RemoteEndPoint.ToString();
            client.NoDelay = true;
            var clientStream = client.GetStream();
            clientStream.ReadTimeout = 5000;
            var received = new byte[TokenLength];
            var count = 0;
            try
            {
                while (count < received.Length)
                {
                    var read = clientStream.Read(received, count, received.Length - count);
                    if (read <= 0) break;
                    count += read;
                }
            }
            catch (IOException)
            {
                Log("rejected " + peer + " (token timeout)");
                return;
            }
            if (count != received.Length)
            {
                Log("rejected " + peer + " (missing or short token)");
                return;
            }
            if (!TokensMatch(received, token))
            {
                Log("rejected " + peer + " (wrong token)");
                return;
            }

            clientStream.ReadTimeout = Timeout.Infinite;
            var keepDown = EnableKeepAlive(client);
            upstream = new TcpClient(AddressFamily.InterNetwork);
            upstream.NoDelay = true;
            var keepUp = EnableKeepAlive(upstream);
            try
            {
                upstream.Connect(IPAddress.Loopback, sdbPort);
            }
            catch (Exception error)
            {
                Log("upstream " + peer + " failed: " + error.GetType().Name
                    + (attachedSessions > 0
                        ? "; sdbd refused a second session while " + attachedSessions + " is attached"
                        : "; sdbd refused the session (not listening, or it does not accept this peer)"));
                return;
            }

            var attached = Interlocked.Increment(ref attachedSessions);
            bridged = true;
            var session = new BridgeSession(
                Interlocked.Increment(ref sessionCounter), client, upstream);
            var degraded = keepDown.StartsWith("keepalive ", StringComparison.Ordinal)
                ? (keepUp.StartsWith("keepalive ", StringComparison.Ordinal)
                    ? string.Empty
                    : " WARNING: upstream " + keepUp)
                : " WARNING: " + keepDown;
            Log("authorized " + peer + " (session " + session.Id + ", " + keepDown + ")"
                + (attached > 1
                    ? " WARNING: " + attached + " bridge sessions attached; sdbd serves one at a time"
                    : string.Empty)
                + degraded);
            session.Arm();

            var upstreamStream = upstream.GetStream();
            var toSdbd = Task.Run(() => session.Copy(clientStream, upstreamStream, true));
            var toClient = Task.Run(() => session.Copy(upstreamStream, clientStream, false));
            session.Await();
            session.CloseGracefully();
            if (!Task.WaitAll(new[] { toSdbd, toClient }, CloseGraceMs))
            {
                session.Abort();
            }
            Log(session.Summary(peer));
        }
        catch (Exception error)
        {
            Log("connection " + peer + " failed: " + error.GetType().Name);
        }
        finally
        {
            if (bridged) Interlocked.Decrement(ref attachedSessions);
            try { client.Close(); } catch { }
            if (upstream != null) { try { upstream.Close(); } catch { } }
        }
    }

    private static int ReadSdbPort()
    {
        int parsed;
        if (int.TryParse(Environment.GetEnvironmentVariable("TVROOT_SDB_PORT"), out parsed)
            && parsed >= 1024 && parsed <= 65535)
        {
            return parsed;
        }
        return SdbPort;
    }

    private static bool TokensMatch(byte[] supplied, string expected)
    {
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var difference = 0;
        for (int i = 0; i < TokenLength; i++)
        {
            difference |= supplied[i] ^ expectedBytes[i];
        }
        return difference == 0;
    }

    // The managed SetSocketOption answers TCP_KEEPIDLE, TCP_KEEPINTVL and TCP_KEEPCNT with
    // EOPNOTSUPP on Linux (measured: SocketException "Operation not supported", while the same
    // numbers through libc return 0 and read back). That leaves the two-hour default, which is
    // precisely the setting that must be short: it is what frees sdbd's session slot when a client
    // disappears without a FIN. So these go straight to the kernel, and the result is reported
    // instead of swallowed, because a silent failure here is the wedge we are trying to remove.
    private static string EnableKeepAlive(TcpClient client)
    {
        int handle;
        try
        {
            handle = (int)client.Client.Handle;
        }
        catch (Exception error)
        {
            return "keepalive unavailable (" + error.GetType().Name + ")";
        }

        var enabled = 1;
        if (SetSockOpt(handle, SolSocket, SoKeepAlive, ref enabled, 4) != 0)
        {
            return "keepalive FAILED (errno " + Marshal.GetLastWin32Error() + ")";
        }

        var idle = KeepAliveIdleSeconds;
        var interval = KeepAliveIntervalSeconds;
        var probes = KeepAliveProbeCount;
        var refused = string.Empty;
        if (SetSockOpt(handle, IpprotoTcp, TcpKeepIdle, ref idle, 4) != 0) refused += " TCP_KEEPIDLE";
        if (SetSockOpt(handle, IpprotoTcp, TcpKeepInterval, ref interval, 4) != 0) refused += " TCP_KEEPINTVL";
        if (SetSockOpt(handle, IpprotoTcp, TcpKeepCount, ref probes, 4) != 0) refused += " TCP_KEEPCNT";

        return refused.Length == 0
            ? "keepalive " + idle + "s/" + interval + "s x" + probes
            : "keepalive at platform default, kernel refused" + refused;
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "setsockopt")]
    private static extern int SetSockOpt(
        int sockfd, int level, int optname, ref int optval, int optlen);

    // One bridged connection: two pumps, an activity stamp, and a watchdog. The stamp is what tells
    // a shell a person left open from a half-open socket whose client stopped existing, and neither
    // of them is allowed to hold sdbd's session forever.
    private sealed class BridgeSession
    {
        private const int Running = 0;
        private const int ClientClosed = 1;
        private const int UpstreamClosed = 2;
        private const int IdleExpired = 3;
        private const int ClientFailure = 4;
        private const int UpstreamFailure = 5;

        private readonly TcpClient client;
        private readonly TcpClient upstream;
        private readonly ManualResetEvent finished = new ManualResetEvent(false);
        private readonly long started = Environment.TickCount64;
        private long lastActivity = Environment.TickCount64;
        private long bytesToSdbd;
        private long bytesToClient;
        private int reason = Running;
        private Timer watchdog;

        public BridgeSession(long id, TcpClient client, TcpClient upstream)
        {
            Id = id;
            this.client = client;
            this.upstream = upstream;
        }

        public long Id { get; private set; }

        public void Arm()
        {
            watchdog = new Timer(CheckIdle, null, idleTimeoutMs / 4, idleTimeoutMs / 4);
        }

        public void Copy(NetworkStream source, NetworkStream destination, bool towardsSdbd)
        {
            try
            {
                var buffer = new byte[65536];
                while (true)
                {
                    var count = source.Read(buffer, 0, buffer.Length);
                    if (count <= 0)
                    {
                        Interlocked.CompareExchange(
                            ref reason, towardsSdbd ? ClientClosed : UpstreamClosed, Running);
                        break;
                    }
                    Interlocked.Exchange(ref lastActivity, Environment.TickCount64);
                    if (towardsSdbd) Interlocked.Add(ref bytesToSdbd, count);
                    else Interlocked.Add(ref bytesToClient, count);
                    destination.Write(buffer, 0, count);
                }
            }
            catch
            {
                // sdbd resets connections in ordinary use, and so does a client that is killed. An
                // ending is not a crash: record why and let the handler tear both legs down.
                Interlocked.CompareExchange(
                    ref reason, towardsSdbd ? ClientFailure : UpstreamFailure, Running);
            }
            finally
            {
                finished.Set();
            }
        }

        public void Await()
        {
            finished.WaitOne();
        }

        public void CloseGracefully()
        {
            StopWatchdog();
            try { client.Close(); } catch { }
            try { upstream.Close(); } catch { }
        }

        public void Abort()
        {
            // A peer that never answers the FIN still has to go. Linger-off close sends RST, the
            // kernel drops the connection, and the session slot comes back without a reboot.
            LingerOff(client);
            LingerOff(upstream);
            try { client.Close(); } catch { }
            try { upstream.Close(); } catch { }
        }

        public string Summary(string peer)
        {
            StopWatchdog();
            var age = (Environment.TickCount64 - started) / 1000;
            // Naming the leg that ended is the point: `sdbd closed, 0s, 0B to sdbd` is the
            // television refusing a second session on a loopback connection it accepted and hung up
            // on, while `client closed` is us. They were indistinguishable and a reboot was spent
            // telling them apart.
            var why = reason == IdleExpired
                ? "idle"
                : reason == UpstreamClosed
                    ? "sdbd closed"
                    : reason == ClientClosed
                        ? "client closed"
                        : reason == UpstreamFailure
                            ? "socket error on sdbd"
                            : reason == ClientFailure
                                ? "socket error on client"
                                : "ended";
            return "closed " + peer + " (session " + Id + ", " + why + ", " + age + "s, "
                + Interlocked.Read(ref bytesToSdbd) + "B to sdbd, "
                + Interlocked.Read(ref bytesToClient) + "B to client)";
        }

        private void CheckIdle(object state)
        {
            if (Environment.TickCount64 - Interlocked.Read(ref lastActivity) < idleTimeoutMs) return;
            Interlocked.CompareExchange(ref reason, IdleExpired, Running);
            finished.Set();
            // Both pumps are parked in Read; only closing the sockets releases them.
            Abort();
        }

        private void StopWatchdog()
        {
            var timer = watchdog;
            watchdog = null;
            if (timer != null) { try { timer.Dispose(); } catch { } }
        }

        private static void LingerOff(TcpClient tcp)
        {
            try { tcp.Client.LingerState = new LingerOption(true, 0); } catch { }
        }
    }

    private static void Log(string message)
    {
        var line = "[" + DateTime.UtcNow.ToString("o") + "] bridge " + message + "\n";
        lock (LogLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(LogPath, line);
            }
            catch { }
        }
        try { Console.WriteLine(line.TrimEnd()); } catch { }
    }
}
