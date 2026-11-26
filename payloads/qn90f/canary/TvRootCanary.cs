// Runtime canary for the QN90F's trimmed .NET host. Deliberately touches nothing vendor:
// no P/Invoke into Samsung libraries, no blocking accept, no pipe on the exit code (M94:
// `exit=$?` behind a pipeline reports head's status and hid a segfault). Its only job is to
// answer whether this host will load a netstandard2.0 dependency, bind a socket, and run
// managed crypto -- the three things an IL sshd needs -- and to exit with a checkable code.
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

internal static class Program
{
    private static int Main()
    {
        Console.WriteLine($"canary=1 pid={Process.GetCurrentProcess().Id} ptr={IntPtr.Size} "
            + $"framework={Environment.Version} lib={TvRootCanaryLib.Describe()}");
        try
        {
            var listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            Console.WriteLine($"listener=ok port={((IPEndPoint)listener.LocalEndpoint).Port}");
            listener.Stop();
        }
        catch (Exception e) { Console.WriteLine($"listener=err {e.GetType().Name}:{e.Message}"); }
        try
        {
            using (var rsa = RSA.Create(2048))
            {
                var p = rsa.ExportParameters(true);
                Console.WriteLine($"rsa=ok modulus={p.Modulus.Length} bytes");
            }
        }
        catch (Exception e) { Console.WriteLine($"rsa=err {e.GetType().Name}:{e.Message}"); }
        try
        {
            using (var sha = SHA256.Create())
            {
                Console.WriteLine($"sha256=ok {BitConverter.ToString(sha.ComputeHash(new byte[8])).Substring(0, 12)}");
            }
        }
        catch (Exception e) { Console.WriteLine($"sha256=err {e.GetType().Name}:{e.Message}"); }
        Console.WriteLine("canary=done");
        return 7;
    }
}
