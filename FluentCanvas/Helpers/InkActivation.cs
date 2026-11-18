using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace FluentCanvas.Helpers
{
    internal static class InkActivation
    {
        public static string[] SelectFiles(IEnumerable<string> args) =>
            args.Where(arg => arg != null && (arg.EndsWith(".icart", StringComparison.OrdinalIgnoreCase) ||
                                              arg.EndsWith(".icstk", StringComparison.OrdinalIgnoreCase))).ToArray();

        public static string[] FromCommandLine(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments)) return Array.Empty<string>();
            // A synthetic argv[0] lets the shell apply argument (not executable)
            // quoting rules to the first path, even when the API omits the exe.
            var argv = CommandLineToArgvW("FluentCanvas " + arguments, out int count);
            if (argv == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception();
            try
            {
                var args = new string[count - 1];
                for (int i = 1; i < count; i++)
                    args[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size));
                return SelectFiles(args);
            }
            finally
            {
                LocalFree(argv);
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
