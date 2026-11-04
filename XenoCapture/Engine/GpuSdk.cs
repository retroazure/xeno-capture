using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
// Every P/Invoke in this assembly targets a Windows system library.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace XenoCapture.Engine;
internal static class GpuSdk
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint CreateVsr(nint device,[MarshalAs(UnmanagedType.LPWStr)] string model,[MarshalAs(UnmanagedType.LPWStr)] string cache,out nint session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint EvaluateVsr(nint session,nint input,nint output,uint iw,uint ih,uint ow,uint oh,uint quality);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint SynchronizeVsr(nint session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Destroy(nint session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CreateFlow(nint device,nint first,nint second,uint width,uint height,out nint session,out nint forward,out nint backward,out nint forwardCost,out nint backwardCost,out uint gridWidth,out uint gridHeight);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int EvaluateFlow(nint session,uint previous,uint current);
    internal sealed class Api
    {
        readonly nint module;
        internal readonly string Directory;
        internal readonly CreateVsr VsrCreate; internal readonly EvaluateVsr VsrEvaluate; internal readonly Destroy VsrDestroy;internal readonly SynchronizeVsr VsrSynchronize;
        internal readonly CreateFlow FlowCreate; internal readonly EvaluateFlow FlowEvaluate; internal readonly Destroy FlowDestroy;
        internal Api(string directory)
        {
            Directory = directory;
            var assembly = typeof(GpuSdk).Assembly;
            string expected = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "XenoGpuBridgeSha256")?.Value
                ?? throw new InvalidDataException("The build is missing its GPU bridge checksum.");
            string path = Path.Combine(Directory, "XenoGpuBridge.dll");
            using var verified = OpenVerifiedDll(path, expected);
            module = NativeLibrary.Load(path, assembly, DllImportSearchPath.System32);
            try
            {
                T Function<T>(string name) where T : Delegate =>
                    Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));
                VsrCreate = Function<CreateVsr>("XenoVsrCreate");
                VsrEvaluate = Function<EvaluateVsr>("XenoVsrEvaluate");
                VsrDestroy = Function<Destroy>("XenoVsrDestroy");
                VsrSynchronize = Function<SynchronizeVsr>("XenoVsrSynchronize");
                FlowCreate = Function<CreateFlow>("XenoOfCreate");
                FlowEvaluate = Function<EvaluateFlow>("XenoOfEvaluate");
                FlowDestroy = Function<Destroy>("XenoOfDestroy");
            }
            catch { NativeLibrary.Free(module); throw; }
        }

    }

    internal static FileStream OpenVerifiedModel(string directory) => OpenVerifiedDll(
        Path.Combine(directory, "nvngx_vsr.dll"),
        "C3D88EEA5FF7A548EDEFA66414CF6E77464D0947277C904F324DD23ABF58A1ED");

    static FileStream OpenVerifiedDll(string path, string expected)
    {
        // Keep the verified file open without write/delete sharing while it is loaded.
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (file.Length is < 1 or > 64 * 1024 * 1024
                || !string.Equals(Convert.ToHexString(SHA256.HashData(file)), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(Path.GetFileName(path) + " does not match the packaged GPU component.");
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    static readonly Lazy<Api> library = new(() => new Api(Path.Combine(AppContext.BaseDirectory, "gpu")));
    internal static Api Library=>library.Value;
}