// Screen-capture spike for the QN90F, run on the set under `/usr/bin/dotnet`.
//
// Three modes, because the routes disagree about who owns the frame:
//   secvideo  secvideo_api_capture_screen(bufferType, position, p)      -- vendor C wrapper
//   ppi       ppi_video_capture_get_screen_post_yuv(in, out)            -- same backend, other door
//   tz        libteec client, replicating CaptureManager::Initialize/ConnectToTZ/RunCaptureTZ
//
// `tz` is the route that matters: the vendor's own screen grab does not go through
// libvideo-capture at all, it asks the scaler TA over the TEE client API. Every constant
// below was read out of /usr/bin/sa-ui-detector for the current firmware:
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
//   command id = a scaler id (GetScalerID/rm_find_device_id), so it is swept, not guessed.
//
// Usage: dotnet Qn90fScreenCaptureProbe.dll <cmdId> <position> <pathPrefix> [mode]
// Writes <pathPrefix>-<cmdId>-plane0.bin / -plane1.bin when the TA returns buffers.

using System;
using System.IO;
using System.Runtime.InteropServices;

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
            Console.WriteLine("usage: Qn90fScreenCaptureProbe.dll <cmdId> <position> <pathPrefix> [secvideo|ppi|tz]");
            return 2;
        }

        int cmdId = int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
        int position = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        string prefix = args[2];
        string mode = args.Length > 3 ? args[3] : "secvideo";

        Console.WriteLine($"probe mode={mode} cmdId={cmdId} position={position} "
            + $"is64bit={Environment.Is64BitProcess} ptr={IntPtr.Size}");

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

        return mode == "tz"
            ? RunTrustedZone(cmdId, prefix)
            : RunVendorWrapper(cmdId, position, prefix, mode, instance);
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
