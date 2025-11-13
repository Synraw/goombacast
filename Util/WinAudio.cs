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
        /// <summary>
        /// Attempts to set the default audio endpoint for a running process.
        /// Returns true if the underlying undocumented API reported success.
        /// </summary>
        public static bool TrySetDeviceForProcess(int processId, string deviceFriendlyNameOrId, ERole role = ERole.eMultimedia)
        {
            if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
            if (string.IsNullOrWhiteSpace(deviceFriendlyNameOrId)) throw new ArgumentNullException(nameof(deviceFriendlyNameOrId));

            // 1) Resolve deviceId from friendly name or exact id
            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            var match = devices.FirstOrDefault(d =>
                string.Equals(d.ID, deviceFriendlyNameOrId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.FriendlyName, deviceFriendlyNameOrId, StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                // try contains match on friendly name
                match = devices.FirstOrDefault(d => d.FriendlyName.IndexOf(deviceFriendlyNameOrId, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (match == null)
            {
                // device not found
                return false;
            }

            string deviceId = match.ID;

            // 2) Get an AppUserModelID for the process (may be null)
            string? appUserModelId = null;
            try
            {
                appUserModelId = AppUserModelHelper.GetAppUserModelIdForProcess(processId);
            }
            catch
            {
                // ignore; we'll try fallbacks
            }

            // 3) If we have an AppUserModelID, try the documented helper that uses PolicyConfigVista
            if (!string.IsNullOrWhiteSpace(appUserModelId))
            {
                try
                {
                    if (PolicyConfig.SetDefaultDeviceForApp(appUserModelId, deviceId, role))
                        return true;
                }
                catch
                {
                    // fall through to fallbacks
                }
            }

            // 4) Fallbacks — undocumented heuristics some tools use:
            //    - executable name (e.g. "chrome.exe")
            //    - full exe path
            try
            {
                var proc = Process.GetProcessById(processId);
                string exeName = string.Empty;
                string? exePath = null;

                try
                {
                    exePath = proc.MainModule?.FileName;
                    exeName = Path.GetFileName(exePath ?? proc.ProcessName);
                }
                catch
                {
                    // accessing MainModule can fail for system processes / elevated processes
                    exeName = proc.ProcessName + ".exe";
                }

                // Try exeName
                try
                {
                    if (PolicyConfig.SetDefaultDeviceForApp(exeName, deviceId, role))
                        return true;
                }
                catch { }

                // Try full path if available
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    try
                    {
                        if (PolicyConfig.SetDefaultDeviceForApp(exePath, deviceId, role))
                            return true;
                    }
                    catch { }
                }
            }
            catch
            {
                // cannot access process; nothing else to try
            }

            // Nothing worked
            return false;
        }
    }

    // Role values mirror Windows ERole
    public enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }

    // Minimal COM interface exposing the single method we need.
    // GUID and class are the widely‑used (undocumented) values seen in community examples.
    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        // We only declare the method we need. PreserveSig returns HRESULT we can check.
        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, ERole eRole);
    }

    // COM coclass used to create an implementation of IPolicyConfig
    [ComImport]
    [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient
    {
    }

    // Undocumented PolicyConfigVista interface (community examples) that exposes
    // per-application endpoint mapping. This is undocumented and can be fragile.
    [ComImport]
    [Guid("568b9108-44bf-40b4-9006-86afe5b5a620")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfigVista
    {
        // The order/number of methods in the real vtable is larger; we only declare
        // the one we need here. PreserveSig gives us an HRESULT to check.
        [PreserveSig]
        int SetAppDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszAppID, [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ERole role);
    }

    // COM coclass used to create an implementation of PolicyConfigVista interface.
    // This CLSID is commonly used in community samples.
    [ComImport]
    [Guid("294935CE-F637-4E7C-A41B-AB255460B862")]
    class PolicyConfigVistaClient
    {
    }

    // Helper class exposing methods to set default audio devices.
    public static class PolicyConfig
    {
        public static bool SetDefaultDevice(string deviceId, ERole role = ERole.eMultimedia)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                throw new ArgumentNullException(nameof(deviceId));

            var policy = new PolicyConfigClient() as IPolicyConfig;
            if (policy == null)
                throw new InvalidOperationException("Failed to create PolicyConfig COM object.");

            int hr = policy.SetDefaultEndpoint(deviceId, role);

            if (hr == 0) // S_OK
                return true;

            // Convert HRESULT to exception for callers who want detailed failure info
            Marshal.ThrowExceptionForHR(hr);
            return false;
        }

        /// <summary>
        /// Attempts to set the default audio endpoint for a specific application (undocumented).
        /// <para>
        /// appId is the application identifier used by Windows to store per-app device mappings.
        /// This is typically the application's AppUserModelID or an identifier Windows recognizes
        /// for the process. For classic desktop apps there is no stable public mapping API; some
        /// tools use the executable name or AppUserModelID. This API is undocumented and may not
        /// work reliably across Windows versions or for all apps.
        /// </para>
        /// </summary>
        public static bool SetDefaultDeviceForApp(string appId, string deviceId, ERole role = ERole.eMultimedia)
        {
            if (string.IsNullOrWhiteSpace(appId))
                throw new ArgumentNullException(nameof(appId));
            if (string.IsNullOrWhiteSpace(deviceId))
                throw new ArgumentNullException(nameof(deviceId));

            // Create undocumented PolicyConfigVista COM object
            var policy = new PolicyConfigVistaClient() as IPolicyConfigVista;
            if (policy == null)
                throw new InvalidOperationException("Failed to create PolicyConfigVista COM object.");

            int hr = policy.SetAppDefaultEndpoint(appId, deviceId, role);

            if (hr == 0) // S_OK
                return true;

            // Rethrow as exception for caller if desired
            Marshal.ThrowExceptionForHR(hr);
            return false;
        }
    }
}
