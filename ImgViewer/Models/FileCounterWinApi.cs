using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class FileCounterWinApi
{
    public static int CountImagesTopOnly(string folder)
    {
        if (string.IsNullOrEmpty(folder))
            throw new ArgumentNullException(nameof(folder));

        // pattern: "C:\dir\*"
        string pattern = folder.EndsWith("\\", StringComparison.Ordinal)
            ? folder + "*"
            : folder + "\\*";

        int count = 0;

        WIN32_FIND_DATA data;
        using (var hFind = FindFirstFileEx(
                   pattern,
                   FINDEX_INFO_LEVELS.FindExInfoBasic,
                   out data,
                   FINDEX_SEARCH_OPS.FindExSearchNameMatch,
                   IntPtr.Zero,
                   FIND_FIRST_EX_LARGE_FETCH))
        {
            if (hFind.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                // 2 = ERROR_FILE_NOT_FOUND (папка есть, но по маске ничего)
                // 18 = ERROR_NO_MORE_FILES (редко тут)
                if (err == 2) return 0;
                throw new Win32Exception(err);
            }

            do
            {
                // пропускаем директории
                if ((data.dwFileAttributes & FileAttributes_Directory) != 0)
                    continue;

                // data.cFileName — только имя файла, без пути
                if (IsImageFileName(data.cFileName))
                    count++;

            } while (FindNextFile(hFind, out data));

            int last = Marshal.GetLastWin32Error();
            if (last != ERROR_NO_MORE_FILES)
                throw new Win32Exception(last);
        }

        return count;
    }

    // очень быстрый check по расширению без Path.GetExtension / EndsWith
    private static bool IsImageFileName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        int dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1)
            return false;

        int extLen = name.Length - dot; // includes '.'

        // Сравниваем ASCII case-insensitive вручную
        // Поддерживаем: .jpg .jpeg .png .tif .tiff
        if (extLen == 4)
        {
            // .jpg .png .tif
            char c1 = ToLowerAscii(name[dot + 1]);
            char c2 = ToLowerAscii(name[dot + 2]);
            char c3 = ToLowerAscii(name[dot + 3]);

            if (c1 == 'j' && c2 == 'p' && c3 == 'g') return true;
            if (c1 == 'p' && c2 == 'n' && c3 == 'g') return true;
            if (c1 == 't' && c2 == 'i' && c3 == 'f') return true;

            return false;
        }

        if (extLen == 5)
        {
            // .jpeg .tiff
            char c1 = ToLowerAscii(name[dot + 1]);
            char c2 = ToLowerAscii(name[dot + 2]);
            char c3 = ToLowerAscii(name[dot + 3]);
            char c4 = ToLowerAscii(name[dot + 4]);

            if (c1 == 'j' && c2 == 'p' && c3 == 'e' && c4 == 'g') return true;
            if (c1 == 't' && c2 == 'i' && c3 == 'f' && c4 == 'f') return true;

            return false;
        }

        return false;
    }

    private static char ToLowerAscii(char c)
    {
        // только для латиницы A-Z; для расширений этого достаточно
        if (c >= 'A' && c <= 'Z') return (char)(c | 0x20);
        return c;
    }

    // ===== WinAPI =====

    private const int ERROR_NO_MORE_FILES = 18;
    private const int FileAttributes_Directory = 0x10;

    // Это флаг для FindFirstFileEx: ускоряет на некоторых FS/драйверах
    private const int FIND_FIRST_EX_LARGE_FETCH = 0x00000002;

    private enum FINDEX_INFO_LEVELS : int
    {
        FindExInfoStandard = 0,
        FindExInfoBasic = 1
    }

    private enum FINDEX_SEARCH_OPS : int
    {
        FindExSearchNameMatch = 0
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATA
    {
        public int dwFileAttributes;

        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;

        public uint nFileSizeHigh;
        public uint nFileSizeLow;

        public uint dwReserved0;
        public uint dwReserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    // SafeHandle для FindFirstFileEx/FindClose
    private sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeFindHandle() : base(true) { }

        protected override bool ReleaseHandle() => FindClose(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFindHandle FindFirstFileEx(
        string lpFileName,
        FINDEX_INFO_LEVELS fInfoLevelId,
        out WIN32_FIND_DATA lpFindFileData,
        FINDEX_SEARCH_OPS fSearchOp,
        IntPtr lpSearchFilter,
        int dwAdditionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextFile(SafeFindHandle hFindFile, out WIN32_FIND_DATA lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindClose(IntPtr hFindFile);
}
