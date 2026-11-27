// Library survey for the IL sshd decision. Loads the three DevTunnels.Ssh assemblies by file,
// with no compile-time reference, because the point is to learn the real API surface on the
// device rather than guess it: type list, the server-side configuration object, the algorithms
// it would actually offer a client, and the key algorithms available for a host key.
//
// Rules this follows from the last two incidents (M94, M96): never call into a vendor library,
// never block (no accept, no sleep beyond a bounded wait), never hide the exit code behind a
// pipe, and print the failure that happened instead of letting the process die mid-buffer.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class Program
{
    private static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : AppContext.BaseDirectory;
        Console.WriteLine($"sshprobe=1 framework={Environment.Version} ptr={IntPtr.Size} dir={dir}");

        var loaded = new List<Assembly>();
        foreach (string name in new[]
        {
            "Microsoft.DevTunnels.Ssh.dll",
            "Microsoft.DevTunnels.Ssh.Tcp.dll",
            "Microsoft.DevTunnels.Ssh.Keys.dll",
        })
        {
            string path = Path.Combine(dir, name);
            try
            {
                Assembly a = Assembly.LoadFrom(path);
                int types = SafeTypes(a).Length;
                loaded.Add(a);
                Console.WriteLine($"load=ok {name} v={a.GetName().Version} types={types} "
                    + $"len={new FileInfo(path).Length}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"load=ERR {name} {e.GetType().Name}: {e.Message}");
            }
        }
        if (loaded.Count == 0) return 3;

        Assembly core = loaded[0];

        // What the server side looks like: the config object and the session type.
        Type cfgType = Find(core, "SshSessionConfiguration");
        Type sessType = Find(core, "SshServerSession");
        Console.WriteLine($"SshServerSession={(sessType is null ? "MISSING" : sessType.FullName)}");
        if (cfgType is null) { Console.WriteLine("config=MISSING"); return 4; }

        Console.WriteLine("config_ctors=" + string.Join(" | ", cfgType.GetConstructors()
            .Select(c => "(" + string.Join(",", c.GetParameters().Select(p => p.ParameterType.Name)) + ")")));

        object cfg = null;
        try
        {
            ConstructorInfo ctor = cfgType.GetConstructors()
                .OrderBy(c => c.GetParameters().Length).First();
            ParameterInfo[] ps = ctor.GetParameters();
            Console.WriteLine("config_ctor=" + string.Join(",",
                ps.Select(p => p.ParameterType.Name + " " + p.Name)));
            object[] argv = ps.Select(p => p.ParameterType == typeof(bool) ? (object)true : null).ToArray();
            cfg = ctor.Invoke(argv);
            Console.WriteLine($"config=ok args=[{string.Join(",", argv.Select(a => a?.ToString() ?? "null"))}]");
        }
        catch (Exception e)
        {
            Console.WriteLine($"config=ERR {e.GetType().Name}: {e.InnerException?.GetType().Name} {e.InnerException?.Message}");
        }
        if (cfg is null) return 5;

        foreach (string prop in cfgType.GetProperties()
                     .Where(p => p.Name.Contains("Algorithm"))
                     .Select(p => p.Name).Distinct().Take(10))
        {
            PropertyInfo pi = cfgType.GetProperty(prop);
            if (pi is null) { Console.WriteLine($"  {prop}=absent"); continue; }
            try
            {
                object v = pi.GetValue(cfg);
                IEnumerable src = v?.GetType().GetProperty("Keys")?.GetValue(v) as IEnumerable
                    ?? v as IEnumerable;
                string list = src is null ? "?" : string.Join(",",
                    Cast(src).Take(16).Select(AlgoName));
                Console.WriteLine($"  {prop}={list}");
            }
            catch (Exception e) { Console.WriteLine($"  {prop}=ERR {e.GetType().Name}"); }
        }

        // Host key options: which key algorithms can we mint on the set?
        foreach (Type t in loaded.SelectMany(SafeTypes).Where(t => t.IsEnum &&
                    (t.Name.Contains("KeyAlgorithm") || t.Name.Contains("KeyExchange"))).Take(4))
        {
            Console.WriteLine($"enum {t.Name}=" + string.Join(",",
                Enum.GetNames(t).Take(12)));
        }

        Console.WriteLine("sshprobe=done");
        return 11;
    }

    // The dictionaries hold algorithm objects, not wire strings, so ToString() yields a .NET type
    // name; the SSH wire name is the Name property, which is what decides negotiation with a modern
    // OpenSSH client.
    private static string AlgoName(object o)
    {
        if (o is null) return "-";
        try
        {
            object v = o.GetType().GetProperty("Name")?.GetValue(o);
            if (v is not null) return v.ToString();
        }
        catch (Exception) { }
        return o.ToString();
    }

    private static IEnumerable<object> Cast(IEnumerable e)
    {
        foreach (object o in e) yield return o;
    }

    private static Type Find(Assembly a, string name) =>
        SafeTypes(a).FirstOrDefault(t => t.Name == name && t.IsPublic);

    private static Type[] SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t is not null).ToArray(); }
        catch (Exception) { return Type.EmptyTypes; }
    }
}
