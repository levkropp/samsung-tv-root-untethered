// Display-capture probe for the QN90F, driving libdisplay-capture-api.so.0 directly.
//
// Why this library and not the ones already tried: it is a *public native* display-capture API that
// nothing on this firmware links (measured: grep for "libdisplay-capture-api" over /usr/lib and
// /usr/bin returns only the library itself, and no managed assembly references dc_request_*), so no
// vendor process exercises it here. Unlike libtzcapturec -- whose checkProcessWhitelist over
// /proc/self/cmdline rejected every caller -- the gate here is parameter validation only:
// ChekRequestPram checks app<=6, mode<=4 and that the name string is non-empty, nothing else.
//
// ABI, recovered by decompiling sha256 b687e4cd386816edda864084efa24bcd631410c57a0a7e774332c57d70ecea1f
// (Hopper through REA) rather than guessed:
//   dc_app_type_e  0 volt_app  1 web_app  2 game_app  3 efl_app  4 source  5 channel  6 sports
//                  (pointer table at .data.rel.ro 0x16e94, seven R_ARM_RELATIVE entries)
//   _dc_capt_req_data { +0x00 app, +0x04 mode, +0x08 format(0 png, 1 jpg), +0x0c jpeg quality,
//                       +0x10 const char* dir, +0x14 const char* name, +0x18 w in/out,
//                       +0x1c h in/out, +0x20 out w, +0x24 out h, +0x28 char out_path[256] }
//   dc_request_capture_to_file_sync(req): validates the above, access(dir, W_OK), then
//     m_capture_to_file_sync -> force_set_available_size(&w,&h) -> ppicapture(RequestedData*,
//     plane0, plane1, 0x7e900) over two static 960*540 buffers -> generate_buffer ->
//     convert_raw_to_png|convert_raw_to_jpeg("<dir>/<name>.<ext>").
//   dc_request_capture_sync(app, mode, w, h, name, extra): ChekRequestPram then m_capture, which
//     writes under /tmp with a name built from the app table.
// Return codes in the control flow: 0 success, -2 invalid parameters or access(dir) failed,
// 1 from to_file_sync when m_capture_to_file_sync itself fails, and inside that: -4 when
// ppicapture answers 0x20, -6 otherwise, -5 when generate_buffer fails, -10 when the encoder fails.
//
// Marshalling note (this cost one debug cycle): the struct holds raw char*, so the strings have to
// be unmanaged buffers. GCHandle.ToIntPtr hands out the HANDLE, not the pinned address, and passing
// that makes access() read garbage and return -2 for every app/mode combination -- which looks
// exactly like a permission gate and is not one.
//
// Side effects, stated because this runs on a living television: the library writes one vconf key
// via _update_vconf and a file in the directory we pass. It opens no vendor session and holds no
// scaler; the plane buffers are process-static.
//
// Usage:
//   Qn90fDisplayCaptureProbe.dll sweep <dir> [w h]        every app x mode, one line each
//   Qn90fDisplayCaptureProbe.dll one <dir> <name> <app> <mode> <fmt> <w> <h>
//   Qn90fDisplayCaptureProbe.dll sync <app> <mode> <w> <h> <name>
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

internal static class Program
{
    private const int StructBytes = 0x600;
    private const string Lib = "libdisplay-capture-api.so.0";

    [DllImport(Lib, EntryPoint = "dc_request_capture_to_file_sync")]
    private static extern int DcToFile(IntPtr req);

    [DllImport(Lib, EntryPoint = "dc_request_capture_sync")]
    private static extern int DcSync(int app, int mode, int w, int h, string name, string extra);

    private static int Main(string[] args)
    {
        string verb = args.Length > 0 ? args[0] : "sweep";
        try
        {
            switch (verb)
            {
                case "sweep": return Sweep(args);
                case "one": return One(args);
                case "sync": return Sync(args);
                default:
                    Console.WriteLine("usage: sweep <dir> [w h] | one <dir> <name> <app> <mode> <fmt> <w> <h> | sync <app> <mode> <w> <h> <name>");
                    return 2;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("probe_exception=" + e.GetType().Name + " " + e.Message);
            return 1;
        }
    }

    private static int Sweep(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : "/tmp/dcap";
        int w = args.Length > 2 ? ParseInt(args[2], 960) : 960;
        int h = args.Length > 3 ? ParseInt(args[3], 540) : 540;
        try { Directory.CreateDirectory(dir); } catch (Exception e) { Console.WriteLine("mkdir_error=" + e.Message); }
        int ok = 0, tried = 0;
        for (int app = 0; app <= 6; app++)
        {
            for (int mode = 0; mode <= 4; mode++)
            {
                tried++;
                int rc = CallToFile(dir, "dc-" + app + "-" + mode, app, mode, 0, w, h,
                                    out string path, out int ow, out int oh);
                long len = -1;
                try { if (File.Exists(path)) { len = new FileInfo(path).Length; } } catch (Exception) { }
                Console.WriteLine("app={0} mode={1} rc={2} size={3}x{4} file={5} bytes={6}",
                    app, mode, rc, ow, oh, path, len);
                if (rc == 0 && len > 0) { ok++; }
            }
        }
        Console.WriteLine("sweep tried={0} produced={1}", tried, ok);
        return ok > 0 ? 0 : 3;
    }

    private static int One(string[] args)
    {
        if (args.Length < 8) { Console.WriteLine("need <dir> <name> <app> <mode> <fmt> <w> <h>"); return 2; }
        int rc = CallToFile(args[1], args[2], ParseInt(args[3], 0), ParseInt(args[4], 0),
                            ParseInt(args[5], 0), ParseInt(args[6], 960), ParseInt(args[7], 540),
                            out string path, out int ow, out int oh);
        Console.WriteLine("rc={0} size={1}x{2} path={3}", rc, ow, oh, path);
        try
        {
            if (File.Exists(path))
            {
                byte[] head = new byte[24];
                using (FileStream fs = File.OpenRead(path)) { int got = fs.Read(head, 0, head.Length); }
                Console.Write("head=");
                foreach (byte b in head) { Console.Write(b.ToString("x2")); }
                Console.WriteLine();
                Console.WriteLine("png={0} bytes={1}", head[0] == 0x89 && head[1] == 0x50, new FileInfo(path).Length);
            }
        }
        catch (Exception e) { Console.WriteLine("read_error=" + e.Message); }
        return rc;
    }

    private static int Sync(string[] args)
    {
        int app = args.Length > 1 ? ParseInt(args[1], 3) : 3;
        int mode = args.Length > 2 ? ParseInt(args[2], 0) : 0;
        int w = args.Length > 3 ? ParseInt(args[3], 960) : 960;
        int h = args.Length > 4 ? ParseInt(args[4], 540) : 540;
        string name = args.Length > 5 ? args[5] : "tvsync";
        HashSet<string> before = SnapTmp();
        int rc = DcSync(app, mode, w, h, name, null);
        Console.WriteLine("dc_request_capture_sync app={0} mode={1} {2}x{3} name={4} rc={5}", app, mode, w, h, name, rc);
        foreach (string f in SnapTmp())
        {
            if (!before.Contains(f))
            {
                try { Console.WriteLine("new_file={0} bytes={1}", f, new FileInfo(f).Length); }
                catch (Exception) { Console.WriteLine("new_file={0}", f); }
            }
        }
        return rc;
    }

    private static HashSet<string> SnapTmp()
    {
        var set = new HashSet<string>();
        try { foreach (string f in Directory.GetFiles("/tmp")) { set.Add(f); } } catch (Exception) { }
        return set;
    }

    private static int CallToFile(string dir, string name, int app, int mode, int fmt, int w, int h,
                                  out string path, out int outW, out int outH)
    {
        IntPtr req = Marshal.AllocHGlobal(StructBytes);
        IntPtr dirP = Marshal.StringToHGlobalAnsi(dir);
        IntPtr nameP = Marshal.StringToHGlobalAnsi(name);
        path = ""; outW = 0; outH = 0;
        try
        {
            for (int i = 0; i < StructBytes; i++) { Marshal.WriteByte(req, i, 0); }
            Marshal.WriteInt32(req, 0x00, app);
            Marshal.WriteInt32(req, 0x04, mode);
            Marshal.WriteInt32(req, 0x08, fmt);
            Marshal.WriteInt32(req, 0x0c, 80);
            Marshal.WriteInt32(req, 0x10, unchecked((int)dirP.ToInt64()));
            Marshal.WriteInt32(req, 0x14, unchecked((int)nameP.ToInt64()));
            Marshal.WriteInt32(req, 0x18, w);
            Marshal.WriteInt32(req, 0x1c, h);
            int rc = DcToFile(req);
            outW = Marshal.ReadInt32(req, 0x18);
            outH = Marshal.ReadInt32(req, 0x1c);
            path = Marshal.PtrToStringAnsi(IntPtr.Add(req, 0x28)) ?? "";
            if (string.IsNullOrEmpty(path)) { path = dir + "/" + name + (fmt == 1 ? ".jpg" : ".png"); }
            return rc;
        }
        finally
        {
            Marshal.FreeHGlobal(req); Marshal.FreeHGlobal(dirP); Marshal.FreeHGlobal(nameP);
        }
    }

    private static int ParseInt(string s, int dflt)
    {
        return int.TryParse(s, out int v) ? v : dflt;
    }
}
