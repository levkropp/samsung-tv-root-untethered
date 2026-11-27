using System;
public static class TvRootCanaryLib
{
    public static string Describe() => "netstandard2.0-lib-ok ptr=" + IntPtr.Size;
}
