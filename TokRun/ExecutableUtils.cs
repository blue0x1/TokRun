using System;
using System.IO;
using System.Text;

namespace TokRun
{
    internal static class ExecutableUtils
    {
        private const ushort IMAGE_SUBSYSTEM_WINDOWS_CUI = 3;

        public static string ResolveExecutablePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(path.Trim('"'));
                if (IsRemoteOrUrlPath(expanded))
                    return null;

                if (Path.IsPathRooted(expanded) && File.Exists(expanded))
                    return Path.GetFullPath(expanded);

                if (File.Exists(expanded))
                    return Path.GetFullPath(expanded);

                var buffer = new StringBuilder(32768);
                IntPtr filePart;
                uint n = NativeMethods.SearchPath(null, expanded, null, buffer.Capacity, buffer, out filePart);
                if (n > 0 && n < buffer.Capacity && File.Exists(buffer.ToString()))
                    return buffer.ToString();
            }
            catch { }

            return null;
        }

        public static bool IsRemoteOrUrlPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
                return false;

            string value = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (value.StartsWith(@"\\"))
                return true;

            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && !uri.IsFile;
        }

        public static bool IsConsoleApplication(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var br = new BinaryReader(fs))
                {
                    if (fs.Length < 256 || br.ReadUInt16() != 0x5A4D) // MZ
                        return false;

                    fs.Position = 0x3C;
                    int peOffset = br.ReadInt32();
                    if (peOffset <= 0 || peOffset + 0x80 > fs.Length)
                        return false;

                    fs.Position = peOffset;
                    if (br.ReadUInt32() != 0x00004550) // PE\0\0
                        return false;

                    fs.Position = peOffset + 4 + 20 + 0x44; // OptionalHeader.Subsystem
                    ushort subsystem = br.ReadUInt16();
                    return subsystem == IMAGE_SUBSYSTEM_WINDOWS_CUI;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
