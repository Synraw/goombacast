using System;
using System.Runtime.InteropServices;
using System.Text;

namespace GoombaCast.Util
{
    internal static class AppUserModelHelper
    {
        // PROPERTYKEY for PKEY_AppUserModel_ID {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3} pid 5
        private static readonly PROPERTYKEY PKEY_AppUserModel_ID = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

        public static string? GetAppUserModelIdForProcess(int processId)
        {
            var hwnd = FindTopLevelWindowForProcess(processId);
            if (hwnd == IntPtr.Zero) return null;
            return GetAppUserModelIdForWindow(hwnd);
        }

        public static string? GetAppUserModelIdForWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;

            Guid iid = typeof(IPropertyStore).GUID;
            int hr = SHGetPropertyStoreForWindow(hwnd, ref iid, out IPropertyStore? store);
            if (hr != 0 || store == null) return null;

            try
            {
                PropVariant pv = default;
                // Workaround CS0199: copy static readonly field to a local variable
                PROPERTYKEY key = PKEY_AppUserModel_ID;
                store.GetValue(ref key, out pv);
                try
                {
                    // VT_LPWSTR (31) and VT_BSTR (8) are common string types here
                    if (pv.vt == (ushort)VarEnum.VT_LPWSTR || pv.vt == (ushort)VarEnum.VT_BSTR)
                    {
                        return Marshal.PtrToStringUni(pv.ptr) ?? string.Empty;
                    }

                    return null;
                }
                finally
                {
                    PropVariantClear(ref pv);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }

        private static IntPtr FindTopLevelWindowForProcess(int processId)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hWnd, lParam) =>
            {
                _ = GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == processId && IsWindowVisible(hWnd) && GetWindow(hWnd, GW_OWNER) == IntPtr.Zero)
                {
                    found = hWnd;
                    return false; // stop enumeration
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private const int GW_OWNER = 4;

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

        [DllImport("shell32.dll")]
        private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, out IPropertyStore ppv);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant pvar);

        [ComImport]
        [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint cProps);
            void GetAt(uint iProp, out PROPERTYKEY pkey);
            void GetValue(ref PROPERTYKEY key, out PropVariant pv);
            void SetValue(ref PROPERTYKEY key, ref PropVariant pv);
            void Commit();
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct PROPERTYKEY
        {
            public readonly Guid fmtid;
            public readonly uint pid;
            public PROPERTYKEY(Guid fmtid, uint pid) { this.fmtid = fmtid; this.pid = pid; }
        }

        // Minimal PROPVARIANT for the string case
        [StructLayout(LayoutKind.Explicit)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(2)] public ushort wReserved1;
            [FieldOffset(4)] public ushort wReserved2;
            [FieldOffset(6)] public ushort wReserved3;
            [FieldOffset(8)] public IntPtr ptr; // POINT to string when vt == VT_LPWSTR
            // other union fields omitted
        }

        private enum VarEnum : ushort
        {
            VT_EMPTY = 0,
            VT_BSTR = 8,
            VT_LPWSTR = 31
        }
    }
}