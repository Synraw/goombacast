using NAudio.CoreAudioApi;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace GoombaCast.Util
{
    public static class WinAudio
    {
        // Role values mirror Windows ERole
        public enum ERole
        {
            eConsole = 0,
            eMultimedia = 1,
            eCommunications = 2
        }

        /// <summary>
        /// Attempts to set the default audio endpoint for a running process.
        /// Returns true if the underlying undocumented API reported success.
        /// </summary>
        public static bool TrySetDeviceForProcess(int processId, string deviceFriendlyNameOrId, ERole role = ERole.eMultimedia)
        {
            if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
            if (string.IsNullOrWhiteSpace(deviceFriendlyNameOrId)) throw new ArgumentNullException(nameof(deviceFriendlyNameOrId));

            var deviceId = ResolveDeviceId(deviceFriendlyNameOrId);
            if (deviceId is null)
                return false;

            // Try AppUserModelID first
            if (TryGetAppUserModelId(processId, out var appUserModelId) && 
                TrySetDevice(appUserModelId, deviceId, role))
                return true;

            // Fallback to executable name and path
            return TrySetDeviceByProcessFallbacks(processId, deviceId, role);
        }

        private static string? ResolveDeviceId(string deviceFriendlyNameOrId)
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            // Try exact match first (ID or friendly name)
            var match = devices.FirstOrDefault(d =>
                string.Equals(d.ID, deviceFriendlyNameOrId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.FriendlyName, deviceFriendlyNameOrId, StringComparison.OrdinalIgnoreCase));

            // Fallback to partial match on friendly name
            match ??= devices.FirstOrDefault(d =>
                d.FriendlyName.Contains(deviceFriendlyNameOrId, StringComparison.OrdinalIgnoreCase));

            return match?.ID;
        }

        private static bool TryGetAppUserModelId(int processId, out string? appUserModelId)
        {
            appUserModelId = null;
            try
            {
                appUserModelId = AppUserModelHelper.GetAppUserModelIdForProcess(processId);
                return !string.IsNullOrWhiteSpace(appUserModelId);
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySetDevice(string? appId, string? deviceId, ERole role)
        {
            try
            {
                return PolicyConfig.SetDefaultDeviceForApp(appId, deviceId, role);
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySetDeviceByProcessFallbacks(int processId, string deviceId, ERole role)
        {
            try
            {
                var proc = Process.GetProcessById(processId);
                var (exeName, exePath) = GetProcessExecutableInfo(proc);

                // Try executable name
                if (!string.IsNullOrWhiteSpace(exeName) && TrySetDevice(exeName, deviceId, role))
                    return true;

                // Try full path
                if (!string.IsNullOrWhiteSpace(exePath) && TrySetDevice(exePath, deviceId, role))
                    return true;
            }
            catch
            {
                // Cannot access process
            }

            return false;
        }

        private static (string exeName, string? exePath) GetProcessExecutableInfo(Process process)
        {
            try
            {
                var exePath = process.MainModule?.FileName;
                var exeName = Path.GetFileName(exePath ?? process.ProcessName);
                return (exeName, exePath);
            }
            catch
            {
                // MainModule can fail for system/elevated processes
                return (process.ProcessName + ".exe", null);
            }
        }
    }

    /// <summary>
    /// Minimal COM interface exposing the SetDefaultEndpoint method.
    /// GUID and class are widely-used (undocumented) values from community examples.
    /// </summary>
    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, WinAudio.ERole eRole);
    }

    /// <summary>
    /// COM coclass used to create an implementation of IPolicyConfig.
    /// </summary>
    [ComImport]
    [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    internal class PolicyConfigClient
    {
    }

    /// <summary>
    /// Undocumented PolicyConfig interface that exposes per-application endpoint mapping.
    /// This API is undocumented and may not work reliably across Windows versions.
    /// </summary>
    [ComImport]
    [Guid("568b9108-44bf-40b4-9006-86afe5b5a620")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfigNT
    {
        [PreserveSig]
        int SetAppDefaultEndpoint(
            [MarshalAs(UnmanagedType.LPWStr)] string pszAppID,
            [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            WinAudio.ERole role);
    }


    /// <summary>
    /// Helper class exposing methods to set default audio devices.
    /// </summary>
    public static class PolicyConfig
    {
        // Multiple CLSIDs known to work on different Windows versions
        private static readonly Guid[] PolicyConfigCLSIDs = new[]
        {
            new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"), // Windows 7/8/10
            new Guid("294935CE-F637-4E7C-A41B-AB255460B862"), // Vista/7/8/10/11 (most reliable)
            new Guid("2A1CB9F0-71BC-4D73-AA2F-7B3F3C673808")  // Windows 10 build 20H1+
        };

        public static bool SetDefaultDevice(string? deviceId, WinAudio.ERole role = WinAudio.ERole.eMultimedia)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                throw new ArgumentNullException(nameof(deviceId));

            foreach (var clsid in PolicyConfigCLSIDs)
            {
                try
                {
                    var type = Type.GetTypeFromCLSID(clsid);
                    if (type == null) continue;

                    var policy = Activator.CreateInstance(type) as IPolicyConfig;
                    if (policy == null) continue;

                    int hr = policy.SetDefaultEndpoint(deviceId, role);
                    if (hr == 0) return true;
                    
                    Marshal.ThrowExceptionForHR(hr);
                }
                catch
                {
                    continue; // Try next CLSID
                }
            }

            throw new InvalidOperationException("Failed to create PolicyConfig COM object with any known CLSID.");
        }

        /// <summary>
        /// Sets the default audio endpoint for a specific application.
        /// Works on Windows Vista through Windows 11, despite being undocumented.
        /// </summary>
        public static bool SetDefaultDeviceForApp(string? appId, string? deviceId, WinAudio.ERole role = WinAudio.ERole.eMultimedia)
        {
            if (string.IsNullOrWhiteSpace(appId))
                throw new ArgumentNullException(nameof(appId));
            if (string.IsNullOrWhiteSpace(deviceId))
                throw new ArgumentNullException(nameof(deviceId));

            foreach (var clsid in PolicyConfigCLSIDs)
            {
                try
                {
                    var type = Type.GetTypeFromCLSID(clsid);
                    if (type == null) continue;

                    var policy = Activator.CreateInstance(type) as IPolicyConfigNT;
                    if (policy == null) continue;

                    int hr = policy.SetAppDefaultEndpoint(appId, deviceId, role);
                    if (hr == 0) return true;

                    Marshal.ThrowExceptionForHR(hr);
                }
                catch
                {
                    continue; // Try next CLSID
                }
            }

            throw new InvalidOperationException("Failed to create PolicyConfigVista COM object with any known CLSID.");
        }
    }
}
