// API dump for writing the sshd prototype. The library loads on the set (M97); this prints the
// exact server-side surface so the prototype compiles against reality instead of a README.
//   arg1 = directory holding the three assemblies
//   arg2 = type-name substring; prefix with '!' to include non-public types
using System;
using System.Linq;
using System.Reflection;

internal static class Program
{
    private const string Core = "Microsoft.DevTunnels.Ssh.dll";
    private const string Keys = "Microsoft.DevTunnels.Ssh.Keys.dll";
    private const string Tcp = "Microsoft.DevTunnels.Ssh.Tcp.dll";

    private static string Dir { get; set; }

    private static int Main(string[] args)
    {
        Dir = args.Length > 0 ? args[0] : AppContext.BaseDirectory;
        string[] files = { Core, Keys, Tcp };

        if (args.Length > 1)
        {
            foreach (string f in files) { Members(Load(f), args[1]); }
            return 0;
        }

        foreach (string f in files)
        {
            Console.WriteLine($"## {f}");
            foreach (Type t in Types(Load(f)).Where(t => t.IsPublic).OrderBy(t => t.Name))
            {
                Console.WriteLine($"  {t.Name}");
            }
        }
        return 0;
    }

    private static Assembly Load(string name)
    {
        try { return Assembly.LoadFrom(System.IO.Path.Combine(Dir, name)); }
        catch (Exception) { return null; }
    }

    private static Type[] Types(Assembly a)
    {
        if (a is null) return Type.EmptyTypes;
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t is not null).ToArray(); }
        catch (Exception) { return Type.EmptyTypes; }
    }

    private static void Members(Assembly a, string needle)
    {
        if (a is null) return;
        bool loose = needle.StartsWith('!');
        string want = needle.TrimStart('!');
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
            BindingFlags.Instance | BindingFlags.DeclaredOnly;

        foreach (Type t in Types(a).Where(t => t.Name.Contains(want) && (loose || t.IsPublic)).Take(60))
        {
            Console.WriteLine($"### {a.GetName().Name} :: {t.FullName}"
                + (t.IsAbstract ? " [abstract]" : "") + (t.IsEnum ? " [enum]" : ""));
            if (t.IsEnum)
            {
                Console.WriteLine("   values: " + string.Join(",", Enum.GetNames(t).Take(24)));
                continue;
            }
            foreach (PropertyInfo p in t.GetProperties(F).Take(16))
                Console.WriteLine($"   prop {(p.GetMethod?.IsVirtual == true ? "virtual " : "")}{Full(p.PropertyType)} {p.Name}");
            foreach (MethodInfo m in t.GetMethods(F).Where(m => !m.IsSpecialName).Take(18))
                Console.WriteLine($"   {(m.IsStatic ? "static " : "")}{(m.IsVirtual ? "virtual " : "")}m {Full(m.ReturnType)} {m.Name}("
                    + string.Join(",", m.GetParameters().Select(p => Full(p.ParameterType))) + ")");
            foreach (EventInfo e in t.GetEvents(F).Take(12))
                Console.WriteLine($"   event {Full(e.EventHandlerType)} {e.Name}");
            foreach (ConstructorInfo c in t.GetConstructors(F).Take(6))
                Console.WriteLine("   ctor (" + string.Join(",",
                    c.GetParameters().Select(p => Full(p.ParameterType))) + ")");
        }
    }

    private static string Full(Type? t)
    {
        if (t is null) return "?";
        if (!t.IsGenericType) return t.FullName ?? t.Name;
        return (t.GetGenericTypeDefinition().FullName ?? t.Name) + "<" + string.Join(",",
            t.GetGenericArguments().Select(x => x.FullName ?? x.Name)) + ">";
    }
}
