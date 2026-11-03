using Microsoft.Win32;

namespace NodeRunner.Data.Installation.Sources;

// Safe, read-only registry access. Returns null on any failure or on non-Windows systems.
internal static class RegistryHelper
{
    public static string? ReadMachineString(string subKey, string valueName)
        => OperatingSystem.IsWindows() ? ReadString(RegistryHive.LocalMachine, subKey, valueName) : null;

    public static string? ReadUserString(string subKey, string valueName)
        => OperatingSystem.IsWindows() ? ReadString(RegistryHive.CurrentUser, subKey, valueName) : null;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ReadString(RegistryHive hive, string subKey, string valueName)
    {

        try
        {
            // Registry64 view: on 64-bit Windows this sees both native keys and, through
            // the explicit WOW6432Node paths we pass, the 32-bit keys written by launchers.
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
