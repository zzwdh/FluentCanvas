using System;
using System.IO;

namespace FluentCanvas.Helpers
{
    internal static class AtomicFile
    {
        // Stage beside the destination so the commit is a same-volume rename.
        public static void Write(string path, Action<Stream> write, string backupPath = null)
        {
            string destination = Path.GetFullPath(path);
            string temporary = Path.Combine(Path.GetDirectoryName(destination), $".{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    write(stream);
                    stream.Flush(true);
                }

                if (backupPath != null && File.Exists(destination))
                    File.Replace(temporary, destination, backupPath);
                else
                    File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }
}
