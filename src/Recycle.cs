// Send a file to the Recycle Bin. The Windows counterpart of
// FileManager.trashItem, and the only delete the port ever performs.
using System;
using System.IO;

namespace LineDry
{
    internal static class Recycle
    {
        public static bool Send(string path, bool quietly)
        {
            if (!File.Exists(path)) return false;
            Native.SHFILEOPSTRUCT op = new Native.SHFILEOPSTRUCT();
            op.wFunc = Native.FO_DELETE;
            // The marshaler appends one terminating null; the API wants two.
            op.pFrom = path + "\0";
            op.pTo = null;
            op.fFlags = (ushort)(Native.FOF_ALLOWUNDO
                | (quietly ? (Native.FOF_NOCONFIRMATION | Native.FOF_SILENT | Native.FOF_NOERRORUI) : 0));
            try
            {
                int rc = Native.SHFileOperation(ref op);
                return rc == 0 && !op.fAnyOperationsAborted && !File.Exists(path);
            }
            catch { return false; }
        }
    }
}
