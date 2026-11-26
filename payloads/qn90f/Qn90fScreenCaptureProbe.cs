// Screen-capture spike for the QN90F, run on the set under `/usr/bin/dotnet`.
//
// Modes, in the order the routes were actually tried on T-RSMFUABC-0090:
//   cap       VideoCapture::getScreenPostYUV(in, out) from libcapi-video-capture.so.0 -- the SAFE
//             layer and the one to use, because it owns the secvideo_capture_param marshalling.
//             Measured with Litefin live at 3840x2160: rc=-2 after a flat ~1030 ms for all 21
//             id/slot combinations, before and after lockGlobal(type,id) (which itself answers -1),
//             with the plane fields returned untouched. A constant that ignores every parameter is
//             a fixed wait, not validation: the capture grant never arrives for a caller that has
//             not claimed the scaler from the resource manager -- which the vendor's own client
//             links (libtv-resource-manager.so.1) and this probe does not.
//   capsweep  the same call across candidate scaler-device ids, printing vconf playback_info first
//   drm       open /dev/dri/card<n> and DRM_IOCTL_GET_CAP. card0..2 are crw-rw-rw- root display
//             (226:0/1/2), so the node itself needs no privilege; the capture ioctl is another ask.
//   secvideo  secvideo_api_capture_screen(bufferType, position, p) -- DANGEROUS. With a zeroed p the
//             implementation writes through the caller's null plane pointers and corrupts the heap:
//             `*** Error in Process /usr/bin/dotnet: malloc(): invalid size (unsorted) ***`.
//   ppi       ppi_video_capture_get_screen_post_yuv(in, out) -- same backend, other door, same fault.
//   tz|tzsweep|tzauto|tzlist  libteec client replicating CaptureManager::Initialize/ConnectToTZ/
//             RunCaptureTZ. DEAD END, kept for the record: every step succeeds (InitializeContext,
//             OpenSession on UUID 58d50002-0006-000c-bbe7-707db6e8f17f, two AllocateSharedMemory,
//             lockGlobal) and TEEC_InvokeCommand then answers 0xFFFF000B (BUSY) for ids 0..3 and
//             0xFFFF0006 (BAD_PARAMETERS) for 4..18, because the backend gates on caller identity:
//             libtzcapturec.so carries checkProcessWhitelist over /proc/self/cmdline with an
//             allow-list of vendor test binaries that this firmware does not ship. Do not spoof it.
//   vconfwatch <seconds>  sample memory/tdm_video/playback_info_scaler_0..3
//
// Historical constants below were read out of /usr/bin/sa-ui-detector for this firmware:
//   TEEC_InitializeContext(NULL, &ctx)                       Initialize, 0x4b1f-0x4b23
//   TEEC_OpenSession(ctx, &session, &uuid, TEE_LOGIN_PUBLIC, NULL, NULL, NULL)
//                                                            Initialize, 0x4b2b-0x4b3f
//   uuid 58d50002-0006-000c-bbe7-707db6e8f17f               .rodata 0xbf40
//   shared memory: record {buffer, size, flags}, size 0x7e900 = 960*540, flags 3
//                                                            ConnectToTZ, 0x492d-0x4953
//   operation: +0x04 paramTypes 0x0e0e0201, +0x08/+0x0c = 960/540 in,
//              +0x14/+0x18 = width/height answered by the TA, +0x20/+0x24/+0x28 =
//              memref(parent,size,offset) for plane 1, +0x2c.. for plane 2
//                                                            RunCaptureTZ worker, 0x509d-0x50d5
//   command id = a scaler id. Measured on T-RSMFUABC-0090 with live playback: ids 0..3 answer
//   0xFFFF000B (BUSY) and id >= 4 answers 0xFFFF0006 (BAD_PARAMETERS), so 0..3 is the entire
//   command space -- and GetValidScalerId() (0x4e25) treats "a session holds this scaler" as the
//   *selection* criterion, reading memory/tdm_video/playback_info_scaler_0..3 via vconf_get_str
//   and returning the index whose value carries a four-character marker. Hence tzauto.
//
// Usage: dotnet Qn90fScreenCaptureProbe.dll <cmdId> <position> <pathPrefix> [mode]
// Writes <pathPrefix>-<cmdId>-plane0.bin / -plane1.bin when the TA returns buffers.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Threading;

internal static class Program
{
    private const string Lib = "libvideo-capture.so.0";
    private const string Teec = "libteec.so";
    private const int StructBytes = 0x100;
    private const int PlaneBytes = 0x7e900; // 960 * 540, exactly what the vendor allocates
    private const int FrameWidth = 960;
    private const int FrameHeight = 540;

    // TEEC_Context and TEEC_Session are 4 bytes apart in the vendor object, but the client
    // library is free to grow them; every record gets a generous zeroed arena instead.
    private const int RecordBytes = 0x80;

    [DllImport(Lib, EntryPoint = "_ZN13IVideoCapture11getInstanceEv", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr get_instance();

    [DllImport(Lib, EntryPoint = "secvideo_api_capture_screen", CallingConvention = CallingConvention.Cdecl)]
    private static extern int secvideo_capture_screen(int bufferType, int position, IntPtr p);

    [DllImport(Lib, EntryPoint = "ppi_video_capture_get_screen_post_yuv", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ppi_get_screen_post_yuv(IntPtr input, IntPtr output);

    [DllImport(Lib, EntryPoint = "_ZN13IVideoCapture16isProtectCaptureERi", CallingConvention = CallingConvention.Cdecl)]
    private static extern int is_protect_capture(IntPtr self, IntPtr outFlag);

    [DllImport(Lib, EntryPoint = "_ZN13IVideoCapture10lockGlobalEv", CallingConvention = CallingConvention.Cdecl)]
    private static extern int lock_global(IntPtr self);

    [DllImport(Lib, EntryPoint = "_ZN13IVideoCapture12unlockGlobalEv", CallingConvention = CallingConvention.Cdecl)]
    private static extern int unlock_global(IntPtr self);

    // libcapi-video-capture is the C++ layer the vendor's own clients link; it owns the
    // secvideo_capture_param marshalling that the raw C entry point expects pre-filled.
    [DllImport("libcapi-video-capture.so.0", EntryPoint = "_ZN12VideoCapture11getInstanceEv",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr capi_get_instance();

    [DllImport("libcapi-video-capture.so.0",
        EntryPoint = "_ZN12VideoCapture16getScreenPostYUVERKNS_11InputParamsERNS_12OutputParamsE",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern int capi_screen_post(IntPtr self, IntPtr input, IntPtr output);

    [DllImport("libc.so.6", EntryPoint = "open", CallingConvention = CallingConvention.Cdecl,
        SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc.so.6", EntryPoint = "close", CallingConvention = CallingConvention.Cdecl)]
    private static extern int close(int fd);

    [DllImport("libc.so.6", EntryPoint = "ioctl", CallingConvention = CallingConvention.Cdecl,
        SetLastError = true)]
    private static extern int ioctl(int fd, uint request, IntPtr arg);

    private const int O_RDWR = 2;

    [DllImport("libcapi-video-capture.so.0",
        EntryPoint = "_ZN12VideoCapture10lockGlobalENS_15CaptureLockTypeEi",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern int capi_lock_global(IntPtr self, int type, int id);

    [DllImport(Teec, EntryPoint = "TEEC_InitializeContext", CallingConvention = CallingConvention.Cdecl)]
    private static extern int teec_initialize_context(IntPtr name, IntPtr context);

    [DllImport(Teec, EntryPoint = "TEEC_FinalizeContext", CallingConvention = CallingConvention.Cdecl)]
    private static extern void teec_finalize_context(IntPtr context);

    [DllImport(Teec, EntryPoint = "TEEC_OpenSession", CallingConvention = CallingConvention.Cdecl)]
    private static extern int teec_open_session(
        IntPtr context,
        IntPtr session,
        IntPtr uuid,
        uint connectionMethod,
        IntPtr connectionData,
        IntPtr sessionData,
        IntPtr returnOrigin);

    [DllImport(Teec, EntryPoint = "TEEC_CloseSession", CallingConvention = CallingConvention.Cdecl)]
    private static extern void teec_close_session(IntPtr session);

    [DllImport(Teec, EntryPoint = "TEEC_AllocateSharedMemory", CallingConvention = CallingConvention.Cdecl)]
    private static extern int teec_allocate_shared_memory(
        IntPtr context, IntPtr sharedMemory, uint size, uint flags);

    [DllImport(Teec, EntryPoint = "TEEC_ReleaseSharedMemory", CallingConvention = CallingConvention.Cdecl)]
    private static extern void teec_release_shared_memory(IntPtr sharedMemory);

    [DllImport(Teec, EntryPoint = "TEEC_InvokeCommand", CallingConvention = CallingConvention.Cdecl)]
    private static extern int teec_invoke_command(
        IntPtr session, uint commandId, IntPtr operation, IntPtr returnOrigin);

    private static readonly byte[] Uuid =
    {
        0x02, 0x00, 0xd5, 0x58, 0x06, 0x00, 0x0c, 0x00,
        0xbb, 0xe7, 0x70, 0x7d, 0xb6, 0xe8, 0xf1, 0x7f,
    };

    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("usage: Qn90fScreenCaptureProbe.dll <cmdId|seconds> <idOrPosition> "
                + "<pathPrefix> [cap|drm|secvideo|ppi|tz|tzsweep|tzauto|tzlist|vconfwatch]");
            return 2;
        }

        int cmdId = int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
        string idSpec = args[1];
        int position = int.TryParse(idSpec, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
        string prefix = args[2];
        string mode = args.Length > 3 ? args[3] : "secvideo";

        Console.WriteLine($"probe mode={mode} idArg={idSpec} position={position} "
            + $"uid={getuid()} gid={getgid()} is64bit={Environment.Is64BitProcess} ptr={IntPtr.Size}");

        if (mode == "cap" || mode == "drm" || mode == "capsweep")
        {
            if (mode == "cap") return RunCapiScreenPost(position, prefix);
            if (mode == "drm") return RunDrm(card: position);
            return RunCapiSweep(prefix);
        }

        IntPtr instance = get_instance();
        Console.WriteLine($"IVideoCapture::getInstance -> 0x{instance:x}");
        if (instance == IntPtr.Zero)
        {
            return 3;
        }

        IntPtr flag = Marshal.AllocHGlobal(4);
        Marshal.WriteInt32(flag, -1);
        int protectRc = is_protect_capture(instance, flag);
        Console.WriteLine($"isProtectCapture -> rc={protectRc} flag=0x{Marshal.ReadInt32(flag):x8}");

        return mode switch
        {
            "tz" => RunTrustedZone(cmdId, prefix),
            "tzsweep" => RunSweep(prefix),
            "tzauto" => RunAuto(prefix),
            "tzlist" => RunIdList(idSpec, prefix),
            "vconfwatch" => RunVconfWatch(cmdId),
            _ => RunVendorWrapper(cmdId, position, prefix, mode, instance),
        };
    }

    /// <summary>
    /// The route the vendor's own C++ clients use. `setParameters` in libcapi copies exactly the
    /// first 0x20 bytes of VideoCapture::InputParams into IVideoCapture::InputParams and lifts four
    /// words (0x28/0x2c/0x30/0x34) out of the caller's OutputParams, so the caller owns the plane
    /// destinations; `updateParameters` then permutes 0x00..0x44 back out. Both candidate slots are
    /// filled with pointers into one real NV12 buffer here, because the offsets were recovered from
    /// the copy sites, not from a header, and a wrong guess must not be a null dereference.
    /// </summary>
    private static int RunCapiScreenPost(int scaler, string prefix)
    {
        const int InBytes = 0x20;
        const int OutBytes = 0x48;
        const int Width = 1920;
        const int Height = 1080;
        int yBytes = Width * Height;
        int cBytes = yBytes / 2;
        int frameBytes = yBytes + cBytes;

        IntPtr buf = Marshal.AllocHGlobal(frameBytes);
        IntPtr inp = Marshal.AllocHGlobal(InBytes);
        IntPtr outp = Marshal.AllocHGlobal(OutBytes);
        for (int i = 0; i < InBytes; i++) Marshal.WriteByte(inp, i, 0);
        for (int i = 0; i < OutBytes; i++) Marshal.WriteByte(outp, i, 0);

        IntPtr y = buf;
        IntPtr c = IntPtr.Add(buf, yBytes);
        Marshal.WriteInt32(inp, 8, Width);
        Marshal.WriteInt32(inp, 12, Height);
        Marshal.WriteIntPtr(inp, 16, y);
        Marshal.WriteIntPtr(inp, 20, c);
        Marshal.WriteIntPtr(outp, 0x28, y);
        Marshal.WriteIntPtr(outp, 0x2c, c);
        Marshal.WriteInt32(outp, 0x30, yBytes);
        Marshal.WriteInt32(outp, 0x34, cBytes);

        IntPtr self = capi_get_instance();
        Console.WriteLine($"VideoCapture::getInstance -> 0x{self:x}");
        if (self == IntPtr.Zero) return 4;

        long t0 = DateTime.UtcNow.Ticks;
        int rc = capi_screen_post(self, inp, outp);
        double ms = (DateTime.UtcNow.Ticks - t0) / TimeSpan.TicksPerMillisecond;
        Console.WriteLine($"getScreenPostYUV(scaler={scaler}) -> rc={rc} in {ms:F0} ms");

        for (int o = 0; o < OutBytes; o += 4)
        {
            Console.WriteLine($"  out+0x{o:x2} = 0x{Marshal.ReadInt32(outp, o):x8}");
        }

        int limit = Math.Min(frameBytes, 1 << 20);
        int nz = 0;
        for (int i = 0; i < limit; i++) if (Marshal.ReadByte(buf, i) != 0) nz++;
        Console.WriteLine($"frame buffer: {limit} scanned, {nz} non-zero, wrote {prefix}-capi.bin");
        File.WriteAllBytes(prefix + "-capi.bin", BytesOf(buf, frameBytes));
        return rc;
    }

    /// <summary>
    /// rc=-2 came back with every plane field untouched, and measured while an app was already
    /// playing, so the refusal is a parameter problem, not an idle panel. The impl logs "Error: '%d'
    /// is not valid Scaler ID. Plz use valid Scaler ID Device id!!" and GetScalerID() special-cases
    /// 0x4a/0x4b, while lockGlobal(CaptureLockType,int) precedes the impl's "Capture Lock Taken type
    /// %s, plane %s" line. So: report what the set says is live, then put the id in each plausible
    /// slot, then take the lock and retry. Twelve calls, one process, about a second each.
    /// </summary>
    private static int RunCapiSweep(string prefix)
    {
        const int InBytes = 0x20;
        const int OutBytes = 0x48;
        const int Width = 1920;
        const int Height = 1080;
        int yBytes = Width * Height;
        int frameBytes = yBytes + yBytes / 2;

        for (int s = 0; s < 4; s++)
        {
            IntPtr pv = vconf_get_str($"memory/tdm_video/playback_info_scaler_{s}");
            string live = pv == IntPtr.Zero ? "(null)" : (Marshal.PtrToStringAnsi(pv) ?? "(empty)");
            Console.WriteLine($"scaler{s}: {live}");
        }

        IntPtr buf = Marshal.AllocHGlobal(frameBytes);
        IntPtr inp = Marshal.AllocHGlobal(InBytes);
        IntPtr outp = Marshal.AllocHGlobal(OutBytes);
        IntPtr y = buf;
        IntPtr c = IntPtr.Add(buf, yBytes);
        IntPtr self = capi_get_instance();
        Console.WriteLine($"VideoCapture::getInstance -> 0x{self:x}");
        if (self == IntPtr.Zero) return 4;

        int[] ids = { 0, 1, 74, 75 };
        int[] slots = { 0x00, 0x04, 0x1c };
        bool stop = false;
        for (int pass = 0; pass < 2 && !stop; pass++)
        {
            if (pass == 1)
            {
                int lr = capi_lock_global(self, 0, 0);
                Console.WriteLine($"-- pass 2: lockGlobal(type=0,id=0) -> rc={lr}");
                stop = true;
            }
            foreach (int slot in slots)
            {
                foreach (int id in ids)
                {
                    for (int i = 0; i < InBytes; i++) Marshal.WriteByte(inp, i, 0);
                    for (int i = 0; i < OutBytes; i++) Marshal.WriteByte(outp, i, 0);
                    for (int i = 0; i < frameBytes; i++) Marshal.WriteByte(buf, i, 0);
                    Marshal.WriteInt32(inp, 8, Width);
                    Marshal.WriteInt32(inp, 12, Height);
                    Marshal.WriteIntPtr(inp, 16, y);
                    Marshal.WriteIntPtr(inp, 20, c);
                    Marshal.WriteInt32(inp, slot, id);
                    Marshal.WriteIntPtr(outp, 0x28, y);
                    Marshal.WriteIntPtr(outp, 0x2c, c);
                    Marshal.WriteInt32(outp, 0x30, yBytes);
                    Marshal.WriteInt32(outp, 0x34, yBytes / 2);
                    long t0 = DateTime.UtcNow.Ticks;
                    int rc = capi_screen_post(self, inp, outp);
                    double ms = (DateTime.UtcNow.Ticks - t0) / TimeSpan.TicksPerMillisecond;
                    int limit = Math.Min(frameBytes, 1 << 18);
                    int nz = 0;
                    for (int i = 0; i < limit; i++) if (Marshal.ReadByte(buf, i) != 0) nz++;
                    Console.WriteLine($"id@0x{slot:x2}=0x{id:x} -> rc={rc} {ms:F0}ms nz={nz}/{limit}"
                        + $" out[0x10]=0x{Marshal.ReadInt32(outp, 0x10):x8}"
                        + $" out[0x14]=0x{Marshal.ReadInt32(outp, 0x14):x8}");
                    if (nz > 0)
                    {
                        File.WriteAllBytes($"{prefix}-hit-{slot:x}-{id}.bin", BytesOf(buf, frameBytes));
                        Console.WriteLine($"  FRAME WRITTEN -> {prefix}-hit-{slot:x}-{id}.bin");
                        return 0;
                    }
                }
            }
        }
        return 7;
    }

    /// <summary>
    /// Sanity check for the DRM door: the SDP capture is driven through drmIoctl on a card node, and
    /// card0..2 are world read/write on this firmware. This only opens the node and asks it for its
    /// resources, which is read-only and cannot block, to prove the node is usable by our uid before
    /// any capture ioctl is attempted.
    /// </summary>
    private static int RunDrm(int card)
    {
        string path = $"/dev/dri/card{card}";
        int fd = open(path, O_RDWR);
        if (fd < 0)
        {
            Console.WriteLine($"open {path} -> fd={fd} errno={Marshal.GetLastWin32Error()}");
            return 5;
        }
        // DRM_IOCTL_GET_CAP = _IOR('d', DRM_COMMAND_BASE + 4, struct drm_get_cap) and
        // struct drm_get_cap is two u64 {capability, value} -- 16 bytes, read-only, no user
        // pointers, so it is the safest possible proof that this uid can drive a card node.
        // capability 0 is DRM_CAP_DUMB_BUFFER.
        IntPtr arena = Marshal.AllocHGlobal(16);
        for (int i = 0; i < 16; i++) Marshal.WriteByte(arena, i, 0);
        int rc = ioctl(fd, 0x80106444, arena);
        Console.WriteLine($"open {path} -> fd={fd}; DRM_IOCTL_GET_CAP rc={rc} "
            + $"dumbBuffer={Marshal.ReadInt32(arena, 8)} errno={Marshal.GetLastWin32Error()}");
        // And the vendor's own protected-capture query, one nr past the generic GET_CAP family.
        Console.WriteLine($"(card{card} opened; capture ioctls are the next step, not this one)");
        close(fd);
        return rc == 0 ? 0 : 6;
    }

    private static byte[] BytesOf(IntPtr p, int len)
    {
        byte[] a = new byte[len];
        Marshal.Copy(p, a, 0, len);
        return a;
    }

    /// <summary>
    /// Sweep candidate scaler ids in one process, because the vendor does not hardcode the command
    /// id either: GetScalerID() maps a scaler name through rm_find_device_id(), and
    /// CaptureManager::GetValidScalerId() exists precisely to skip ids the resource manager reports
    /// as taken. Measured on T-RSMFUABC-0090: ids 0..3 answer TEEC 0xFFFF000B (BUSY) and id 4
    /// answers 0xFFFF0006 (BAD_PARAMETERS), so the range below covers the table's extent
    /// (its largest case constant was 0x12) while stopping at the first usable plane.
    /// </summary>
    private static int RunSweep(string prefix)
    {
        int last = 1;
        for (int id = 0; id <= 18; id++)
        {
            last = RunTrustedZone(id, prefix);
            if (last == 0)
            {
                Console.WriteLine($"sweep: scaler {id} produced planes");
                return 0;
            }
        }

        Console.WriteLine($"sweep: no scaler id 0..18 produced planes (last rc={last})");
        return last;
    }

    [DllImport("libvconf.so.0")]
    private static extern IntPtr vconf_get_str(string key);

    /// <summary>
    /// Read the vconf records the vendor uses to choose a scaler, then try every id they imply
    /// (the four indices themselves are always tried, since the marker's meaning is not yet known).
    /// Values are printed verbatim because they are the television's own statement of which scaler
    /// is carrying playback -- that is also the cheapest play/pause signal found so far.
    /// </summary>
    private static int RunAuto(string prefix)
    {
        var candidates = new List<int>();
        for (int i = 0; i < 4; i++)
        {
            string key = $"memory/tdm_video/playback_info_scaler_{i}";
            IntPtr raw = vconf_get_str(key);
            string value = raw == IntPtr.Zero ? string.Empty : (Marshal.PtrToStringUTF8(raw) ?? string.Empty);
            Console.WriteLine($"vconf {key}: [{value.Length}] \"{value}\"");

            int run = -1;
            for (int c = 0; c <= value.Length; c++)
            {
                bool digit = c < value.Length && value[c] >= '0' && value[c] <= '9';
                if (!digit && run >= 0)
                {
                    if (int.TryParse(value.AsSpan(run, c - run), out int v) && v >= 0 && v <= 0x12
                        && !candidates.Contains(v))
                    {
                        candidates.Add(v);
                    }

                    run = -1;
                }
                else if (digit && run < 0)
                {
                    run = c;
                }
            }

            if (!candidates.Contains(i))
            {
                candidates.Add(i);
            }
        }

        Console.WriteLine($"candidate ids: {string.Join(",", candidates)}");
        foreach (int id in candidates)
        {
            if (RunTrustedZone(id, prefix) == 0)
            {
                Console.WriteLine($"auto: scaler {id} produced planes");
                return 0;
            }
        }

        Console.WriteLine("auto: no candidate produced planes");
        return 1;
    }

    [DllImport("libc.so.6")]
    private static extern uint getuid();

    [DllImport("libc.so.6")]
    private static extern uint getgid();

    /// <summary>
    /// Try an explicit id list. The TA's 0..3 are the values GetScalerID() *returns*, but its
    /// comparisons inside show rm_find_device_id yielding 0x4a and 0x4b for two of the scaler
    /// names, so if the command id is a device id rather than an index, those are the ones the
    /// sweep never touched.
    /// </summary>
    private static int RunIdList(string spec, string prefix)
    {
        foreach (string token in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(token.Trim(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int id))
            {
                continue;
            }

            if (RunTrustedZone(id, prefix) == 0)
            {
                Console.WriteLine($"list: scaler {id} produced planes");
                return 0;
            }
        }

        Console.WriteLine($"list: none of [{spec}] produced planes");
        return 1;
    }

    /// <summary>
    /// Timestamped samples of the television's own playback records, so a play/pause/stop sequence
    /// is one root invocation rather than several. Also reads memory/Canalysis-service/ocr_result,
    /// where the content-recognition service parks its OCR text when it is running at all.
    /// </summary>
    private static int RunVconfWatch(int seconds)
    {
        int limit = seconds <= 0 ? 30 : Math.Min(seconds, 300);
        for (int elapsed = 0; elapsed < limit; elapsed += 3)
        {
            Console.WriteLine($"wall={DateTimeOffset.UtcNow.ToUnixTimeSeconds()} t+{elapsed}s");
            for (int i = 0; i < 4; i++)
            {
                IntPtr raw = vconf_get_str($"memory/tdm_video/playback_info_scaler_{i}");
                Console.WriteLine($"  scaler[{i}] {(raw == IntPtr.Zero ? "<unset>" : Marshal.PtrToStringUTF8(raw))}");
            }

            IntPtr ocr = vconf_get_str("memory/Canalysis-service/ocr_result");
            Console.WriteLine($"  ocr_result {(ocr == IntPtr.Zero ? "<unset>" : Marshal.PtrToStringUTF8(ocr))}");
            Thread.Sleep(3000);
        }

        return 0;
    }

    private static int RunVendorWrapper(
        int bufferType, int position, string prefix, string mode, IntPtr instance)
    {
        IntPtr input = Marshal.AllocHGlobal(StructBytes);
        IntPtr output = Marshal.AllocHGlobal(StructBytes);
        IntPtr p = mode == "ppi" ? input : input;
        for (int i = 0; i < StructBytes * 2; i += 4)
        {
            Marshal.WriteInt32((IntPtr)((long)input + i), 0);
            Marshal.WriteInt32((IntPtr)((long)output + i), 0);
        }

        int lockRc = lock_global(instance);
        Console.WriteLine($"lockGlobal -> {lockRc}");
        int rc = mode == "ppi"
            ? ppi_get_screen_post_yuv(input, output)
            : secvideo_capture_screen(bufferType, position, p);
        Console.WriteLine($"capture rc={rc}; unlockGlobal -> {unlock_global(instance)}");

        Dump("p/input", input);
        Dump("out", output);
        string path = prefix + $"-{bufferType}-{position}.struct.bin";
        byte[] blob = new byte[StructBytes * 2];
        for (int i = 0; i < StructBytes * 2; i += 4)
        {
            int v = (i < StructBytes)
                ? Marshal.ReadInt32((IntPtr)((long)input + i))
                : Marshal.ReadInt32((IntPtr)((long)output + (i - StructBytes)));
            Array.Copy(BitConverter.GetBytes(v), 0, blob, i, 4);
        }

        File.WriteAllBytes(path, blob);
        Console.WriteLine($"struct written: {path} ({blob.Length} bytes)");
        return rc;
    }

    private static int RunTrustedZone(int scalerId, string prefix)
    {
        int off = 0;
        IntPtr arena = Marshal.AllocHGlobal(RecordBytes * 6);
        for (int i = 0; i < RecordBytes * 6; i += 4)
        {
            Marshal.WriteInt32((IntPtr)((long)arena + i), 0);
        }

        IntPtr ctx = (IntPtr)((long)arena + off);
        off += RecordBytes;
        IntPtr session = (IntPtr)((long)arena + off);
        off += RecordBytes;
        IntPtr uuid = (IntPtr)((long)arena + off);
        off += RecordBytes;
        IntPtr rec1 = (IntPtr)((long)arena + off);
        off += RecordBytes;
        IntPtr rec2 = (IntPtr)((long)arena + off);
        off += RecordBytes;
        IntPtr op = (IntPtr)((long)arena + off);

        for (int i = 0; i < Uuid.Length; i++)
        {
            Marshal.WriteByte((IntPtr)((long)uuid + i), Uuid[i]);
        }

        // The vendor pre-fills size and flags into the record before allocating.
        Marshal.WriteInt32((IntPtr)((long)rec1 + 4), PlaneBytes);
        Marshal.WriteInt32((IntPtr)((long)rec1 + 8), 3);
        Marshal.WriteInt32((IntPtr)((long)rec2 + 4), PlaneBytes);
        Marshal.WriteInt32((IntPtr)((long)rec2 + 8), 3);

        Marshal.WriteInt32((IntPtr)((long)op + 0x04), unchecked((int)0x0e0e0201));
        Marshal.WriteInt32((IntPtr)((long)op + 0x08), FrameWidth);
        Marshal.WriteInt32((IntPtr)((long)op + 0x0c), FrameHeight);
        Marshal.WriteInt32((IntPtr)((long)op + 0x20), (int)rec1);
        Marshal.WriteInt32((IntPtr)((long)op + 0x24), PlaneBytes);
        Marshal.WriteInt32((IntPtr)((long)op + 0x28), 0);
        Marshal.WriteInt32((IntPtr)((long)op + 0x2c), (int)rec2);
        Marshal.WriteInt32((IntPtr)((long)op + 0x30), PlaneBytes);
        Marshal.WriteInt32((IntPtr)((long)op + 0x34), 0);

        int rc = teec_initialize_context(IntPtr.Zero, ctx);
        Console.WriteLine($"TEEC_InitializeContext -> {rc} ctx=0x{ctx:x}");
        if (rc != 0)
        {
            return rc;
        }

        IntPtr errOrigin = (IntPtr)((long)arena + 0x1c0);
        rc = teec_open_session(ctx, session, uuid, 0, IntPtr.Zero, IntPtr.Zero, errOrigin);
        Console.WriteLine($"TEEC_OpenSession -> {rc} session=0x{session:x} origin=0x{Marshal.ReadInt32(errOrigin):x8}");
        if (rc != 0)
        {
            teec_finalize_context(ctx);
            return rc;
        }

        int a1 = teec_allocate_shared_memory(ctx, rec1, PlaneBytes, 3);
        int a2 = teec_allocate_shared_memory(ctx, rec2, PlaneBytes, 3);
        Console.WriteLine($"AllocateSharedMemory -> {a1}, {a2} "
            + $"buf1=0x{Marshal.ReadInt32((IntPtr)((long)rec1)):x8} buf2=0x{Marshal.ReadInt32((IntPtr)((long)rec2)):x8}");

        IntPtr instance = get_instance();
        Console.WriteLine($"lockGlobal -> {lock_global(instance)}");
        int invoke = teec_invoke_command(session, (uint)scalerId, op, IntPtr.Zero);
        Console.WriteLine($"TEEC_InvokeCommand(scaler {scalerId}) -> {invoke}");
        Console.WriteLine($"unlockGlobal -> {unlock_global(instance)}");

        int outW = Marshal.ReadInt32((IntPtr)((long)op + 0x14));
        int outH = Marshal.ReadInt32((IntPtr)((long)op + 0x18));
        Console.WriteLine($"operation answer: w={outW} h={outH}");

        if (invoke == 0)
        {
            SavePlane(rec1, prefix + $"-{scalerId}-plane0.bin");
            SavePlane(rec2, prefix + $"-{scalerId}-plane1.bin");
        }

        teec_release_shared_memory(rec1);
        teec_release_shared_memory(rec2);
        teec_close_session(session);
        teec_finalize_context(ctx);
        return invoke;
    }

    private static void SavePlane(IntPtr record, string path)
    {
        IntPtr buffer = (IntPtr)Marshal.ReadInt32(record);
        Console.WriteLine($"{path}: buffer=0x{(long)buffer:x}");
        if ((long)buffer == 0)
        {
            return;
        }

        byte[] data = new byte[PlaneBytes];
        Marshal.Copy(buffer, data, 0, PlaneBytes);
        File.WriteAllBytes(path, data);
        long nonZero = 0;
        for (int i = 0; i < data.Length; i += 97)
        {
            if (data[i] != 0)
            {
                nonZero++;
            }
        }

        Console.WriteLine($"  wrote {data.Length} bytes, {nonZero} non-zero samples of "
            + $"{data.Length / 97}");
    }

    private static void Dump(string label, IntPtr ptr)
    {
        Console.WriteLine($"-- {label} --");
        for (int off = 0; off < 0x60; off += 0x10)
        {
            string[] parts = new string[4];
            for (int i = 0; i < 4; i++)
            {
                parts[i] = $"{Marshal.ReadInt32((IntPtr)((long)ptr + off + (4 * i))):x8}";
            }

            Console.WriteLine($"  +0x{off:x2}: {string.Join(" ", parts)}");
        }
    }
}
