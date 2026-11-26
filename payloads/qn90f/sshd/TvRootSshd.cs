// SSH server that runs as managed IL under the television's vendor-signed /usr/bin/dotnet host, so
// it needs no firmware write and no security control disabled: docs/PLAN-manager.md records
// "Installed IL is not signature-checked -- SELP/UEP gate native ELF, not IL". It exists to replace
// the sdb/relay management channel, which costs a full untether per command and wedges sdbd if a
// command outruns the 45 s window (seven mains power cycles so far).
//
// Division of labour measured on 3.12.42 (M97/M98, not assumed):
//   * The library does transport, kex, ciphers, MACs, user-auth and channel multiplexing.
//   * It recognises the "exec"/"shell"/"pty-req"/"exit-status" request types but ships no service
//     that acts on them (core services are Authentication, KeyExchange, Connection; Tcp adds
//     PortForwarding). Its own package description calls the supported extension point
//     "extensible channel request handling (for exec, shell, or custom requests)", and incoming
//     requests arrive as the base ChannelRequestMessage, reinterpreted with SshMessage.ConvertTo.
//   * Channel data is EventHandler<Buffer> + SendAsync(Buffer) + AdjustWindow; SshStream over a
//     server-side session channel is write-only, so the stdio pump below is explicit.
//
// Deliberate limits, each with a reason:
//   * publickey only. Password / none / keyboard-interactive are refused, never implemented.
//   * No PTY. Honouring pty-req needs forkpty(), i.e. fork() inside a live managed runtime; a
//     wedged television costs a mains power cycle (incidents M92/M94). Consequence: `ssh tv 'cmd'`
//     works and `ssh tv` gets a pipe-backed shell with no job control.
//   * No exit-status reply: the library exposes no message type for it (measured: no *Exit* type),
//     so the client sees status 0 regardless. Rely on stdout, not $?, until that changes.
//   * The child is /bin/sh, a vendor-signed binary, so UEP is not involved.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Keys;
using Microsoft.DevTunnels.Ssh.IO;
using Microsoft.DevTunnels.Ssh.Messages;

namespace TvRootSshd;

internal static class Program
{
    // Dictionary entry measured on the set (M97): this library offers rsa-sha2-512, rsa-sha2-256,
    // ecdsa-sha2-nistp384 and ecdsa-sha2-nistp256, and no ed25519.
    private const string HostKeyAlgo = "rsa-sha2-512";
    private static readonly object LogGate = new();
    private static string _logPath = "";

    private static int Main(string[] args)
    {
        var opt = Options.Parse(args);
        _logPath = opt.LogPath;
        Log("start pid={0} port={1} bind={2} hostkey={3} authkeys={4} fw={5} ptr={6}",
            Environment.ProcessId, opt.Port, opt.Bind, opt.HostKeyPath, opt.AuthKeysPath,
            Environment.Version, IntPtr.Size);

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        SshSessionConfiguration config;
        IKeyPair hostKey;
        try
        {
            config = BuildConfig();
            hostKey = LoadOrMintHostKey(config, opt.HostKeyPath);
        }
        catch (Exception e)
        {
            Log("fatal init={0}: {1}", e.GetType().Name, Trim(e.Message, 200));
            return 2;
        }

        if (opt.SelfTest)
        {
            Log("selftest=ok hostkey_type={0}", hostKey.GetType().Name);
            return 0;
        }

        var authorized = new AuthorizedKeys(opt.AuthKeysPath);
        TcpListener listener;
        try
        {
            IPAddress addr = string.IsNullOrEmpty(opt.Bind) ? IPAddress.Any : IPAddress.Parse(opt.Bind);
            listener = new TcpListener(addr, opt.Port);
            listener.Start();
        }
        catch (Exception e)
        {
            Log("fatal listen={0}: {1}", e.GetType().Name, Trim(e.Message, 200));
            return 3;
        }

        Log("listening={0} authorized_entries={1}", listener.LocalEndpoint, authorized.Count);

        // The accept loop is the product: it must stay up unattended for days. A bad session is
        // logged and dropped; nothing here may take the listener down with it.
        try
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (Exception) when (cts.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) { break; }

                Log("accepted remote={0}", client.Client.RemoteEndPoint);
                if (opt.Once)
                {
                    // Gate-3 smoke: prove the accept path and a port stay reachable, then go away.
                    Log("once=ok");
                    client.Close();
                    break;
                }

                // Every session task is observed: an unobserved exception on this trimmed host must
                // never reach a default unhandled handler.
                ServeSession(client, config, hostKey, authorized, cts.Token)
                    .ContinueWith(t => Log("session_end {0}", Describe(t.Exception)),
                        TaskScheduler.Default);
            }
        }
        finally
        {
            listener.Stop();
            Log("stopped");
        }

        return 0;
    }

    /// <summary>Security on, compression off (no compression algorithm is offered at all), reconnect
    /// off (a management shell gains nothing from session resumption).</summary>
    private static SshSessionConfiguration BuildConfig()
    {
        var config = new SshSessionConfiguration(true, false, false);
        Log("config kex=[{0}] hostkeys=[{1}] ciphers=[{2}] macs=[{3}]",
            NamesOf(config.KeyExchangeAlgorithms), NamesOf(config.PublicKeyAlgorithms),
            NamesOf(config.EncryptionAlgorithms), NamesOf(config.HmacAlgorithms));
        return config;
    }

    // The algorithm collections are ICollection<T> of algorithm objects, not dictionaries; the wire
    // name is each object's Name property, which is how M97 read them on the set.
    private static string NamesOf<T>(IEnumerable<T> items) =>
        string.Join(",", items.Select(a => a is null ? "-" : NameOf(a)));

    private static string NameOf(object a)
    {
        try { return a.GetType().GetProperty("Name")?.GetValue(a)?.ToString() ?? a.ToString() ?? "?"; }
        catch (Exception) { return a.ToString() ?? "?"; }
    }

    private static IKeyPair LoadOrMintHostKey(SshSessionConfiguration config, string path)
    {
        if (File.Exists(path))
        {
            // A changed host key means every client's known_hosts trips, so an unreadable key is a
            // hard stop rather than a silent re-mint.
            IKeyPair k = KeyPair.ImportKey(File.ReadAllText(path), null, KeyFormat.Pkcs1, KeyEncoding.Pem);
            Log("hostkey=loaded path={0} fp={1}", path, Fingerprint(k));
            return k;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        PublicKeyAlgorithm? algo = config.PublicKeyAlgorithms
            .FirstOrDefault(a => a is not null && NameOf(a!) == HostKeyAlgo);
        if (algo is null) { throw new InvalidOperationException("no host key algorithm: " + HostKeyAlgo); }

        IKeyPair fresh = algo.GenerateKeyPair(3072);
        File.WriteAllText(path, KeyPair.ExportPrivateKey(fresh, null, KeyFormat.Pkcs1, KeyEncoding.Pem));
        Chmod600(path);
        Log("hostkey=minted algo={0} fp={1}", HostKeyAlgo, Fingerprint(fresh));
        return fresh;
    }

    private static async Task ServeSession(TcpClient client, SshSessionConfiguration config,
        IKeyPair hostKey, AuthorizedKeys authorized, CancellationToken outer)
    {
        // The TraceSource argument is mandatory: null throws ArgumentNullException inside Connect.
        var session = new SshServerSession(config, new TraceSource("tvroot-sshd"));
        session.Credentials = new SshServerCredentials(new[] { hostKey });
        using (outer.Register(() => { _ = session.CloseAsync(SshDisconnectReason.None, "shutdown"); }))
        {
            session.Authenticating += (_, e) =>
            {
                e.AuthenticationTask = Task.Run(() =>
                {
                    // ClientPublicKeyQuery is the unsigned probe OpenSSH sends before committing to
                    // a key; it must be answered or the signed attempt never arrives.
                    bool isKey = e.AuthenticationType == SshAuthenticationType.ClientPublicKey ||
                                 e.AuthenticationType == SshAuthenticationType.ClientPublicKeyQuery;
                    string user = e.Username ?? e.ClientUsername ?? "";
                    if (isKey && e.PublicKey is not null && authorized.Matches(user, e.PublicKey))
                    {
                        Log("auth=ok stage={0} user={1} fp={2}", e.AuthenticationType, user,
                            Fingerprint(e.PublicKey));
                        return new ClaimsPrincipal(new ClaimsIdentity("tvroot-sshd"));
                    }

                    Log("auth=deny stage={0} user={1} fp={2}", e.AuthenticationType, user,
                        e.PublicKey is null ? "none" : Fingerprint(e.PublicKey));
                    return null;
                }, e.Cancellation);
            };

            session.ChannelOpening += (_, e) =>
            {
                // Session channels only: no agent forwarding, no X11, no port forwarding.
                if (!string.Equals(e.Request.ChannelType, "session", StringComparison.Ordinal))
                {
                    e.FailureReason = SshChannelOpenFailureReason.AdministrativelyProhibited;
                    e.FailureDescription = "only session channels are supported";
                    Log("channel_open=reject type={0}", e.Request.ChannelType);
                }
            };

            await session.ConnectAsync(client.GetStream(), outer).ConfigureAwait(false);
            Log("connected remote_software={0}", session.RemoteVersion?.Name ?? "?");

            while (!outer.IsCancellationRequested && !session.IsClosed)
            {
                SshChannel channel;
                try { channel = await session.AcceptChannelAsync(outer).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (Exception e) { Log("channels_end={0}", e.GetType().Name); break; }

                ServeChannel(channel, outer)
                    .ContinueWith(t => Log("channel_end id_error {0}", Describe(t.Exception)),
                        TaskScheduler.Default);
            }
        }
    }

    /// <summary>
    /// The part nobody ships: turn an exec/shell channel request into a running /bin/sh and carry
    /// its stdio over the channel.
    /// </summary>
    private static async Task ServeChannel(SshChannel channel, CancellationToken outer)
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string command = "";
        bool interactive = false;

        // Bytes that arrive before the child exists must not be lost: an exec followed immediately
        // by input is normal for piped usage.
        var inbox = new BlockingCollection<byte[]>(boundedCapacity: 256);
        channel.DataReceived += (_, data) =>
        {
            byte[] bytes = data.ToArray();
            try { channel.AdjustWindow((uint)bytes.Length); } catch (Exception) { }
            if (bytes.Length > 0 && !inbox.IsAddingCompleted)
            {
                try { inbox.Add(bytes, outer); }
                catch (Exception) { }
            }
        };
        // The library surfaces the remote's CHANNEL_EOF through Closed, so EOF must reach the
        // child's stdin: without it `ssh tv 'cat > f' < f` leaves cat waiting forever and only
        // appears to work because the client eventually gives up.
        channel.Closed += (_, __) =>
        {
            started.TrySetResult(false);
            try { inbox.CompleteAdding(); } catch (Exception) { }
        };

        channel.Request += (_, e) =>
        {
            switch (e.RequestType)
            {
                case "exec":
                    command = ReadExecCommand(e.Request);
                    e.IsAuthorized = true;
                    e.ResponseTask = Task.FromResult<SshMessage>(new ChannelSuccessMessage());
                    e.ResponseContinuation = _ => { started.TrySetResult(true); return Task.CompletedTask; };
                    break;
                case "shell":
                    interactive = true;
                    e.IsAuthorized = true;
                    e.ResponseTask = Task.FromResult<SshMessage>(new ChannelSuccessMessage());
                    e.ResponseContinuation = _ => { started.TrySetResult(true); return Task.CompletedTask; };
                    break;
                case "pty-req":
                case "window-change":
                case "env":
                case "signal":
                case "subsystem":
                case "no-more-sessions@openssh.com":
                    // Acknowledged without effect. A PTY is deliberately not allocated (see header);
                    // refusing these would break ordinary clients in confusing ways instead.
                    e.IsAuthorized = true;
                    e.ResponseTask = Task.FromResult<SshMessage>(new ChannelSuccessMessage());
                    break;
                default:
                    e.IsAuthorized = false;
                    e.ResponseTask = Task.FromResult<SshMessage>(new ChannelFailureMessage());
                    Log("channel_request=unsupported type={0}", e.RequestType);
                    break;
            }
        };

        bool go;
        try { go = await started.Task.WaitAsync(TimeSpan.FromMinutes(2), outer).ConfigureAwait(false); }
        catch (Exception) { return; }
        if (!go) { return; }
        Log("exec begin channel={0} interactive={1} window={2} packet={3} cmd={4}",
            channel.ChannelId, interactive, channel.MaxWindowSize, channel.MaxPacketSize,
            Trim(command, 180));

        Process? proc = null;
        var sendGate = new SemaphoreSlim(1, 1);
        try
        {
            proc = StartChild(command, interactive);
            Stream stdin = proc.StandardInput.BaseStream;
            Stream stdout = proc.StandardOutput.BaseStream;
            Stream stderr = proc.StandardError.BaseStream;

            var stdinLoop = Task.Run(() =>
            {
                try
                {
                    foreach (byte[] bytes in inbox.GetConsumingEnumerable(outer))
                    {
                        stdin.Write(bytes, 0, bytes.Length);
                        stdin.Flush();
                    }
                }
                catch (Exception) { /* child closed its stdin: normal at exit */ }
                finally { try { stdin.Close(); } catch (Exception) { } }
            }, outer);

            var pumpOut = PumpAsync(stdout, channel, sendGate, outer);
            var pumpErr = PumpAsync(stderr, channel, sendGate, outer);
            // Both streams must finish before EOF: a child writing to stderr as well as stdout is
            // the normal case, and premature EOF truncates output.
            await Task.WhenAll(pumpOut, pumpErr).ConfigureAwait(false);
            try { proc.WaitForExit(5000); } catch (Exception) { }
            Log("exec end channel={0} exit={1}", channel.ChannelId,
                proc.HasExited ? proc.ExitCode.ToString() : "unknown");
            try { inbox.CompleteAdding(); } catch (Exception) { }
            // CHANNEL_EOF is what tells the client the output is over. The library's SendEofAsync
            // is internal (measured public surface: Request / DataReceived / Closed), and without
            // it an OpenSSH client sits waiting for the channel to end -- a 90 s stall per command
            // with redirected output. Pinned to 3.12.42, and the log says so if the member is ever
            // missing rather than silently hanging every client.
            try
            {
                System.Reflection.MethodInfo? eof = typeof(SshChannel).GetMethod("SendEofAsync",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public);
                if (eof is null)
                {
                    Log("eof=method_missing channel={0}", channel.ChannelId);
                }
                else if (eof.Invoke(channel, new object[] { outer }) is Task eofTask)
                {
                    await eofTask.ConfigureAwait(false);
                    Log("eof=sent channel={0}", channel.ChannelId);
                }
            }
            catch (Exception e)
            {
                Log("eof=failed {0}: {1}", e.GetType().Name, Trim(e.Message, 120));
            }
            // exit-status first, so the client's $? is the child's rather than the 255 OpenSSH
            // reports when a channel closes without one, then close the channel.
            await SendExitStatusAsync(channel, proc, outer).ConfigureAwait(false);
            try { await channel.CloseAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { }
        }
        catch (Exception e)
        {
            Log("exec error={0}: {1}", e.GetType().Name, Trim(e.Message, 200));
            try { await channel.CloseAsync("exec failed", e.Message); } catch (Exception) { }
        }
        finally
        {
            // The child is our own /bin/sh, not vendor state, so reaping it here is safe and stops
            // a process leaking per connection.
            if (proc is not null)
            {
                try { if (!proc.HasExited) { proc.Kill(entireProcessTree: true); } } catch (Exception) { }
                proc.Dispose();
            }
            sendGate.Dispose();
        }
    }

    /// <summary>
    /// Incoming exec requests arrive as the base ChannelRequestMessage; CommandRequestMessage is the
    /// library's typed form, reached through ConvertTo. Both paths are tried because the payload
    /// decoding is what carries the command string.
    /// </summary>
    private static string ReadExecCommand(ChannelRequestMessage? request)
    {
        if (request is null) { return ""; }
        if (request is CommandRequestMessage direct) { return direct.Command ?? ""; }
        try
        {
            CommandRequestMessage? typed = request.ConvertTo<CommandRequestMessage>(false);
            if (typed?.Command is string s) { return s; }
        }
        catch (Exception e)
        {
            Log("exec convert_failed={0}: {1}", e.GetType().Name, Trim(e.Message, 120));
        }

        // Nothing typed worked; report what the framework actually handed over so this can be fixed
        // against the real shape rather than guessed at.
        Log("exec undecoded type={0} text={1}", request.GetType().FullName, Trim(request.ToString(), 200));
        return "";
    }

    private static async Task PumpAsync(Stream source, SshChannel channel, SemaphoreSlim gate,
        CancellationToken outer)
    {
        var buffer = new byte[8192];
        try
        {
            int n;
            while ((n = await source.ReadAsync(buffer, 0, buffer.Length, outer).ConfigureAwait(false)) > 0)
            {
                await gate.WaitAsync(outer).ConfigureAwait(false);
                try
                {
                    await channel.SendAsync(
                        Microsoft.DevTunnels.Ssh.Buffer.From(buffer, 0, n), outer)
                        .ConfigureAwait(false);
                }
                finally { gate.Release(); }
            }
        }
        catch (Exception) { /* stream or channel closed: the child is done */ }
    }

    /// <summary>
    /// Sends SSH_MSG_CHANNEL_REQUEST "exit-status" (RFC 4254 6.10). Microsoft.DevTunnels.Ssh 3.12.42
    /// has no such message type -- measured: no type in any of the three assemblies contains "Exit"
    /// -- and without it OpenSSH logs "Exit status -1" and exits 255 even when every byte of output
    /// arrived. The payload is therefore written over the library's own writer.
    /// </summary>
    private static async Task SendExitStatusAsync(SshChannel channel, Process? proc, CancellationToken ct)
    {
        uint status;
        try { status = proc is not null && proc.HasExited ? unchecked((uint)proc.ExitCode) : 1u; }
        catch (Exception e) { Log("exit_status=unreadable {0}", e.GetType().Name); status = 1u; }

        try
        {
            bool accepted = await channel.RequestAsync(new ExitStatusRequestMessage(status), ct)
                .ConfigureAwait(false);
            Log("exit_status=sent value={0} accepted={1} channel={2}", status, accepted, channel.ChannelId);
        }
        catch (Exception e)
        {
            Log("exit_status=failed {0}: {1}", e.GetType().Name, Trim(e.Message, 120));
        }
    }

    /// <summary>
    /// The message class the library omits. RequestType has no setter, so it goes into its backing
    /// field; the payload rides the virtual OnWrite hook the library exposes for extensions.
    /// </summary>
    private sealed class ExitStatusRequestMessage : ChannelRequestMessage
    {
        private readonly uint _status;

        public ExitStatusRequestMessage(uint status)
        {
            _status = status;
            WantReply = false;
            AssignRequestType(this, "exit-status");
        }

        protected override void OnWrite(ref SshDataWriter writer)
        {
            base.OnWrite(ref writer);
            writer.Write(_status);
        }

        private static void AssignRequestType(ChannelRequestMessage m, string value)
        {
            PropertyInfo? pi = typeof(ChannelRequestMessage).GetProperty("RequestType");
            try
            {
                if (pi is not null && pi.CanWrite) { pi.SetValue(m, value); return; }
            }
            catch (Exception) { }

            FieldInfo? f = typeof(ChannelRequestMessage)
                .GetField("<RequestType>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            if (f is not null) { f.SetValue(m, value); return; }
            Program.Log("exit_status=request_type_unassignable got={0}", m.RequestType ?? "null");
        }
    }

    private static Process StartChild(string command, bool interactive)
    {
        // ArgumentList, not the argument string: ProcessStartInfo's string form is re-parsed into
        // words, which left /bin/sh with an unterminated quote. Passing argv directly also means
        // nothing here re-quotes or mangles what the client asked to run.
        var psi = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = "/",
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(interactive ? "exec /bin/sh" : command);
        psi.Environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin";
        psi.Environment["HOME"] = "/home/owner";
        psi.Environment["LANG"] = "C";
        psi.Environment["TERM"] = "dumb";
        Process? p = Process.Start(psi);
        if (p is null) { throw new InvalidOperationException("process start returned null"); }
        return p;
    }

    // /opt/usr/share/selfroot is mode 0777 on this firmware, so the host key's mode has to be set
    // explicitly. File.SetUnixFileMode is not in this reference set; chmod is a vendor binary.
    private static void Chmod600(string path)
    {
        try
        {
            using Process? p = Process.Start(new ProcessStartInfo("/bin/chmod", "600 " + path)
            { RedirectStandardError = true, UseShellExecute = false });
            if (p is null || !p.WaitForExit(3000) || p.ExitCode != 0)
            {
                Log("hostkey=chmod_incomplete path={0}", path);
            }
        }
        catch (Exception e) { Log("hostkey=chmod_failed {0}", e.GetType().Name); }
    }

    /// <summary>
    /// The OpenSSH wire blob in base64: what an authorized_keys line holds, and the only form on
    /// which identity comparison means anything. The (OpenSsh, Binary) and (OpenSsh, Base64) export
    /// pairs throw NotSupportedException on 3.12.42, so supported exports are tried in order and
    /// the base64 field taken from whichever answers.
    /// </summary>
    internal static string? PublicKeyBlobBase64(IKeyPair key)
    {
        foreach ((KeyFormat f, KeyEncoding e) in new[]
        {
            (KeyFormat.OpenSsh, KeyEncoding.SshBase64),
            (KeyFormat.OpenSsh, KeyEncoding.Base64),
            (KeyFormat.Ssh2, KeyEncoding.Base64),
        })
        {
            try
            {
                string s = KeyPair.ExportPublicKey(key, f, e)?.Trim() ?? "";
                if (s.Length == 0) { continue; }
                return s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                           .FirstOrDefault(p => p.StartsWith("AAAA", StringComparison.Ordinal)) ?? s;
            }
            catch (Exception ex)
            {
                Log("keyexport=tried {0}/{1} {2}", f, e, ex.GetType().Name);
            }
        }
        return null;
    }

    private static string Fingerprint(IKeyPair key)
    {
        string? b64 = PublicKeyBlobBase64(key);
        if (b64 is null) { return "fp-unavailable"; }
        try
        {
            return "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(b64)))
                .TrimEnd('=');
        }
        catch (Exception e) { return "fp-unavailable:" + e.GetType().Name; }
    }

    private static string Describe(AggregateException? agg)
    {
        if (agg is null) { return "clean"; }
        Exception e = agg.GetBaseException();
        return e.GetType().Name + ": " + Trim(e.Message, 180);
    }

    private static string Trim(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");

    internal static void Log(string fmt, params object?[] a)
    {
        string line;
        try { line = string.Format(fmt, a ?? Array.Empty<object>()); }
        catch (Exception) { line = fmt; }
        string text = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") + " " + line;
        lock (LogGate)
        {
            Console.Out.WriteLine(text);
            Console.Out.Flush();
            if (_logPath.Length > 0)
            {
                // A full or read-only log target must never take the server down.
                try { File.AppendAllText(_logPath, text + "\n"); } catch (Exception) { }
            }
        }
    }
}

/// <summary>
/// OpenSSH authorized_keys, re-read on each check so the owner can add or revoke a key without
/// restarting the server. Lines are `&lt;type&gt; &lt;base64-blob&gt; [comment]`; matching is on the wire
/// blob, because that is the whole identity the client proves.
/// </summary>
internal sealed class AuthorizedKeys
{
    private readonly string _path;

    public AuthorizedKeys(string path) { _path = path; }

    public int Count
    {
        get
        {
            try { return Entries().Count; }
            catch (Exception) { return 0; }
        }
    }

    public bool Matches(string user, IKeyPair presented)
    {
        string? presentedB64 = Program.PublicKeyBlobBase64(presented);
        if (presentedB64 is null)
        {
            Program.Log("auth=error export_publickey=no_supported_format");
            return false;
        }

        List<string[]> entries;
        try { entries = Entries(); }
        catch (Exception) { return false; }

        byte[] want = Encoding.ASCII.GetBytes(presentedB64);
        foreach (string[] parts in entries)
        {
            byte[] have = Encoding.ASCII.GetBytes(parts[1]);
            if (have.Length == want.Length && CryptographicOperations.FixedTimeEquals(have, want))
            {
                return true;
            }
        }

        return false;
    }

    // Each entry as (type, blob): a leading `command="..."` option or trailing comment must not be
    // mistaken for the key material, so the blob is chosen as the first base64-looking field.
    private List<string[]> Entries() =>
        File.ReadAllLines(_path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Select(p =>
            {
                int i = Array.FindIndex(p, t => t.StartsWith("AAAA", StringComparison.Ordinal));
                return i < 0 ? Array.Empty<string>() : new[] { i > 0 ? p[0] : "unknown", p[i] };
            })
            .Where(p => p.Length == 2)
            .ToList();
}

internal sealed class Options
{
    public int Port = 22;
    public string Bind = "";
    public string HostKeyPath = "/opt/usr/share/selfroot/sshd/hostkeys/ssh_host_rsa_key";
    public string AuthKeysPath = "/opt/usr/share/selfroot/sshd/authorized_keys";
    public string LogPath = "";
    public bool Once;
    public bool SelfTest;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Value(string d) => i + 1 < args.Length ? args[++i] : d;
            switch (args[i])
            {
                case "--port": o.Port = int.Parse(Value("22")); break;
                case "--bind": o.Bind = Value(""); break;
                case "--host-key": o.HostKeyPath = Value(o.HostKeyPath); break;
                case "--authorized-keys": o.AuthKeysPath = Value(o.AuthKeysPath); break;
                case "--log": o.LogPath = Value(""); break;
                case "--once": o.Once = true; break;
                case "--selftest": o.SelfTest = true; break;
                default: break;
            }
        }
        return o;
    }
}
