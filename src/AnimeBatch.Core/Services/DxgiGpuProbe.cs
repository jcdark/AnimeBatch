using System.Runtime.InteropServices;

namespace AnimeBatch.Core.Services;

/// <summary>Uma placa de vídeo vista pelo DXGI (mesma ordem de adaptadores que o DirectML usa).</summary>
/// <param name="Index">Ordinal do adaptador — é o device id do execution provider DML.</param>
/// <param name="Name">Descrição do adaptador (ex.: "NVIDIA GeForce RTX 5060 Ti").</param>
/// <param name="Vendor">Fabricante: NVIDIA / AMD / Intel / Vendor 0x....</param>
public record DxgiAdapter(int Index, string Name, string Vendor, long DedicatedVideoMemoryBytes)
{
    public bool IsNvidia => Vendor == "NVIDIA";
}

/// <summary>
/// Enumera as placas de vídeo. Caminho PRIMÁRIO: CreateDXGIFactory1 → EnumAdapters1 — o
/// índice aqui é o device id do DirectML (diferente do índice Vulkan do ncnn e do CUDA do
/// nvidia-smi). Fallback: EnumDisplayDevices (nomes SEM ordinais confiáveis) para máquinas
/// onde a ativação COM da factory falha — observado em builds Insider recentes, onde o
/// CreateDXGIFactory1 devolve E_NOINTERFACE para qualquer IID e o D3DKMTQueryAdapterInfo
/// devolve STATUS_INVALID_PARAMETER, enquanto o ORT-DML cria sessões normalmente.
/// Quando o fallback entra, Index = -1 (desconhecido): o motor ONNX decide as GPUs
/// por BENCHMARK (OnnxUpscaleService.BenchmarkGpusAsync), não por nome.
/// A consulta DXGI vai direto na vtable COM (EnumAdapters1 é o slot 12 da IDXGIFactory1 e
/// GetDesc1 o slot 10 da IDXGIAdapter1) — sem dependência externa.
/// </summary>
public static class DxgiGpuProbe
{
    private static readonly Guid Factory1Iid = new("770aae78-f26f-4dba-a829-253c83d1b38e");

    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint Flags;
        public uint LuidLowPart;
        public int LuidHighPart;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int EnumAdapters1Delegate(IntPtr self, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetDesc1Delegate(IntPtr self, IntPtr desc);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ReleaseDelegate(IntPtr self);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICEW lpDisplayDevice, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICEW
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    /// <summary>A ativação da factory DXGI funcionou nesta sessão?</summary>
    public static bool FactoryAvailable { get; private set; } = true;

    /// <summary>
    /// Placas via factory DXGI (ordinais confiáveis). Lista VAZIA = factory indisponível
    /// (builds com a ativação COM quebrada) — use <see cref="AdapterNamesFallback"/>.
    /// </summary>
    public static IReadOnlyList<DxgiAdapter> Enumerate()
    {
        FactoryAvailable = false;
        var list = new List<DxgiAdapter>();
        if (CreateDXGIFactory1(Factory1Iid, out var factory) != 0)
            return list;

        try
        {
            var enumAdapters1 = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(
                VTableSlot(factory, 12));

            var descSize = Marshal.SizeOf<DXGI_ADAPTER_DESC1>();
            var descPtr = Marshal.AllocHGlobal(descSize);
            try
            {
                for (uint i = 0; ; i++)
                {
                    if (enumAdapters1(factory, i, out var adapter) != 0)
                        break;

                    try
                    {
                        var getDesc = Marshal.GetDelegateForFunctionPointer<GetDesc1Delegate>(
                            VTableSlot(adapter, 10));
                        if (getDesc(adapter, descPtr) != 0)
                            continue;
                        var d = Marshal.PtrToStructure<DXGI_ADAPTER_DESC1>(descPtr);
                        if ((d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0)
                            continue; // WARP: não é placa de verdade
                        list.Add(new DxgiAdapter(
                            (int)i,
                            d.Description.TrimEnd('\0'),
                            VendorName(d.VendorId),
                            (long)d.DedicatedVideoMemory));
                    }
                    finally
                    {
                        Release(adapter);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(descPtr);
            }
        }
        finally
        {
            Release(factory);
        }

        FactoryAvailable = true;
        return list;
    }

    /// <summary>
    /// Nomes de adaptadores SEM ordinais (Index = -1): EnumDisplayDevices repete cada placa
    /// por monitor associado, então apenas desduplica nomes consecutivos. Usado só para
    /// exibição/diagnóstico quando a factory DXGI está indisponível.
    /// </summary>
    public static IReadOnlyList<DxgiAdapter> AdapterNamesFallback()
    {
        var names = new List<string>();
        for (uint i = 0; i < 32; i++)
        {
            var dd = new DISPLAY_DEVICEW { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICEW>() };
            if (!EnumDisplayDevicesW(null, i, ref dd, 0))
                break;
            var name = dd.DeviceString?.TrimEnd('\0') ?? "";
            if (name.Length == 0)
                continue;
            if (names.Count > 0 && names[^1] == name)
                continue; // repetição do mesmo adaptador (monitores)
            names.Add(name);
        }

        return names.Select(n => new DxgiAdapter(-1, n, VendorFromName(n), 0)).ToList();
    }

    /// <summary>Índices DXGI das placas NVIDIA; vazio quando a factory não está disponível.</summary>
    public static IReadOnlyList<int> NvidiaIndices() =>
        Enumerate().Where(a => a.IsNvidia).Select(a => a.Index).ToList();

    /// <summary>Descrição legível das placas detectadas (tela de Configurações): ordinais
    /// DXGI quando disponíveis, senão só os nomes (fallback).</summary>
    public static string Describe()
    {
        var adapters = Enumerate();
        if (adapters.Count > 0)
            return string.Join(", ", adapters.Select(a =>
                $"{a.Index} = {a.Name} ({a.DedicatedVideoMemoryBytes / (1024L * 1024 * 1024):0} GB)"));

        var names = AdapterNamesFallback();
        return names.Count > 0
            ? string.Join(", ", names.Select(a => a.Name)) + " (ordem desconhecida)"
            : "nenhuma placa identificada";
    }

    private static string VendorFromName(string name)
    {
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
            return "NVIDIA";
        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
            return "AMD";
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Arc", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Iris", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("UHD", StringComparison.OrdinalIgnoreCase))
            return "Intel";
        return "Desconhecido";
    }

    private static string VendorName(uint vendorId) => vendorId switch
    {
        0x10DE => "NVIDIA",
        0x1002 => "AMD",
        0x8086 => "Intel",
        _ => $"Vendor 0x{vendorId:X4}",
    };

    private static IntPtr VTableSlot(IntPtr comObject, int slot) =>
        Marshal.ReadIntPtr(Marshal.ReadIntPtr(comObject), slot * IntPtr.Size);

    private static void Release(IntPtr comObject)
    {
        var release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(VTableSlot(comObject, 2));
        release(comObject);
    }
}
