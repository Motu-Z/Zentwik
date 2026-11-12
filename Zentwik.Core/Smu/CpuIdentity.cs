using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Zentwik.Core.Smu;

internal sealed class CpuIdentity
{
    private CpuIdentity(string name, string codeName, SmuFamily family, uint familyId, int physicalCores, int logicalCores, int coresPerCcd)
    {
        Name = name;
        CodeName = codeName;
        Family = family;
        FamilyId = familyId;
        PhysicalCores = physicalCores;
        LogicalCores = logicalCores;
        CoresPerCcd = coresPerCcd;
    }

    public string Name { get; }
    public string CodeName { get; }
    public SmuFamily Family { get; }
    public uint FamilyId { get; }
    public int PhysicalCores { get; }
    public int LogicalCores { get; }
    public int CoresPerCcd { get; }

    public int Ccds => CoresPerCcd > 0 ? PhysicalCores / CoresPerCcd : 0;

    public int ThreadsPerCore => PhysicalCores > 0 ? LogicalCores / PhysicalCores : 1;

    public static CpuIdentity Read()
    {
        (string name, uint familyId, uint modelId) = ReadProcessorDescriptor();
        (int physical, int logical) = ReadTopology();

        return new CpuIdentity(
            name,
            ResolveCodeName(familyId, modelId),
            ResolveFamily(familyId, modelId),
            familyId,
            physical,
            logical,
            CoresPerCcdFor(physical));
    }

    /// <summary>标识形如 "AMD64 Family 25 Model 97 Stepping 2"。</summary>
    private static (string Name, uint Family, uint Model) ReadProcessorDescriptor()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key is null)
                return (string.Empty, 0, 0);

            string name = key.GetValue("ProcessorNameString") as string ?? string.Empty;
            string identifier = key.GetValue("Identifier") as string ?? string.Empty;

            uint family = 0;
            uint model = 0;
            string[] parts = identifier.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i + 1 < parts.Length; i++)
            {
                if (parts[i].Equals("Family", StringComparison.OrdinalIgnoreCase) && uint.TryParse(parts[i + 1], out uint f))
                    family = f;
                else if (parts[i].Equals("Model", StringComparison.OrdinalIgnoreCase) && uint.TryParse(parts[i + 1], out uint m))
                    model = m;
            }

            return (name.Trim(), family, model);
        }
        catch (Exception)
        {
            return (string.Empty, 0, 0);
        }
    }

    private static SmuFamily ResolveFamily(uint familyId, uint modelId) => familyId switch
    {
        0x17 => SmuFamily.Zen2,
        0x19 => modelId is 0x61 or 0x74 or 0x78 ? SmuFamily.Zen4 : SmuFamily.Zen3,
        0x1A => SmuFamily.Zen5,
        _ => SmuFamily.Unsupported,
    };

    private static string ResolveCodeName(uint familyId, uint modelId) => (familyId, modelId) switch
    {
        (0x17, 0x71) => "Matisse",
        (0x17, 0x31) => "Castle Peak",
        (0x17, 0x60 or 0x68 or 0x90) => "Renoir",
        (0x19, 0x21) => "Vermeer",
        (0x19, 0x08) => "Chagall",
        (0x19, 0x50) => "Cezanne",
        (0x19, 0x61) => "Raphael",
        (0x19, 0x74 or 0x78) => "Phoenix",
        (0x1A, 0x44) => "Granite Ridge",
        (0x1A, 0x24) => "Strix Point",
        _ => "Unknown",
    };

    /// <summary>
    /// 消费级平台每 CCD 核数：8 核及以下单 CCD，12 核为 2×6，16 核及以上为 2×8。
    /// EPYC 的多 CCX 形态不在支持范围。
    /// </summary>
    private static int CoresPerCcdFor(int physicalCores) => physicalCores switch
    {
        <= 0 => 0,
        <= 8 => physicalCores,
        12 => 6,
        _ => 8,
    };

    private static (int Physical, int Logical) ReadTopology()
    {
        const int RelationProcessorCore = 0;

        uint length = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, 0, ref length);
        if (length == 0)
            return (Environment.ProcessorCount, Environment.ProcessorCount);

        nint buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
                return (Environment.ProcessorCount, Environment.ProcessorCount);

            int physical = 0;
            int logical = 0;
            int offset = 0;

            while (offset + 32 <= length)
            {
                int relationship = Marshal.ReadInt32(buffer + offset);
                int size = Marshal.ReadInt32(buffer + offset + 4);
                if (size <= 0)
                    break;

                if (relationship == RelationProcessorCore)
                {
                    physical++;

                    // PROCESSOR_RELATIONSHIP 从条目偏移 8 起：GroupCount 在 +22，GroupMask 在 +24。
                    int groupCount = Marshal.ReadInt16(buffer + offset + 30);
                    for (int g = 0; g < groupCount; g++)
                        logical += BitOperations.PopCount((ulong)Marshal.ReadInt64(buffer + offset + 32 + (g * 16)));
                }

                offset += size;
            }

            return physical > 0
                ? (physical, Math.Max(logical, physical))
                : (Environment.ProcessorCount, Environment.ProcessorCount);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, nint buffer, ref uint length);
}
