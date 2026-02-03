using System;
using System.Runtime.InteropServices;

namespace ImageScaler
{
    /// <summary>
    /// Native Vista+ folder picker that uses the modern FileOpenDialog with FOS_PICKFOLDERS.
    /// Avoids WPF dependencies and avoids the old FolderBrowserDialog tree view.
    /// </summary>
    internal static class FolderPicker
    {
        private const int HRESULT_CANCELLED = unchecked((int)0x800704C7);

        public static bool TryPickFolder(IntPtr ownerHwnd, string initialFolder, out string selectedFolder)
        {
            selectedFolder = null;

            IFileOpenDialog dialog = null;
            IShellItem resultItem = null;

            try
            {
                dialog = (IFileOpenDialog)new FileOpenDialogRCW();

                // Configure options
                dialog.GetOptions(out uint options);
                options |= (uint)(FOS.FOS_PICKFOLDERS | FOS.FOS_FORCEFILESYSTEM | FOS.FOS_NOCHANGEDIR);
                dialog.SetOptions(options);

                // Set initial folder if provided
                if (!string.IsNullOrWhiteSpace(initialFolder))
                {
                    if (TryCreateShellItemFromPath(initialFolder, out var initialItem))
                    {
                        dialog.SetFolder(initialItem);
                        dialog.SetDefaultFolder(initialItem);
                    }
                }

                int hr = dialog.Show(ownerHwnd);
                if (hr == HRESULT_CANCELLED)
                    return false;
                if (hr != 0)
                    Marshal.ThrowExceptionForHR(hr);

                dialog.GetResult(out resultItem);
                resultItem.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out IntPtr pszString);

                try
                {
                    selectedFolder = Marshal.PtrToStringUni(pszString);
                }
                finally
                {
                    if (pszString != IntPtr.Zero)
                        Marshal.FreeCoTaskMem(pszString);
                }

                return !string.IsNullOrWhiteSpace(selectedFolder);
            }
            catch (COMException ex) when (ex.ErrorCode == HRESULT_CANCELLED)
            {
                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (resultItem != null)
                {
                    try { Marshal.ReleaseComObject(resultItem); } catch { }
                }

                if (dialog != null)
                {
                    try { Marshal.ReleaseComObject(dialog); } catch { }
                }
            }
        }

        private static bool TryCreateShellItemFromPath(string path, out IShellItem shellItem)
        {
            shellItem = null;
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItem).GUID, out shellItem);
            return hr == 0 && shellItem != null;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            out IShellItem ppv);

        [ComImport]
        [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRCW { }

        [ComImport]
        [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog : IFileDialog
        {
            // IFileDialog methods are inherited.

            // IFileOpenDialog methods
            void GetResults(out IShellItemArray ppenum);
            void GetSelectedItems(out IShellItemArray ppsai);
        }

        [ComImport]
        [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr hwndOwner);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName(out IntPtr pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, FDAP fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
        }

        [ComImport]
        [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport]
        [Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemArray { }

        private enum SIGDN : uint
        {
            SIGDN_FILESYSPATH = 0x80058000
        }

        [Flags]
        private enum FOS : uint
        {
            FOS_NOCHANGEDIR = 0x00000008,
            FOS_PICKFOLDERS = 0x00000020,
            FOS_FORCEFILESYSTEM = 0x00000040
        }

        private enum FDAP : uint
        {
            FDAP_BOTTOM = 0x00000000,
            FDAP_TOP = 0x00000001
        }
    }
}
