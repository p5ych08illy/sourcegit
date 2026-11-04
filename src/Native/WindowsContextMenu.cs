using System;
using System.IO;
using System.Runtime.Versioning;

using Microsoft.Win32;

namespace SourceGit.Native
{
    [SupportedOSPlatform("windows")]
    internal static class WindowsContextMenu
    {
        private const string MenuName = "SourceGit.Open";
        private static readonly string[] Objects = ["*", "Directory", @"Directory\Background", "Drive"];

        public static void Install()
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable) ||
                !Path.GetFileName(executable).Equals("SourceGit.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Run SourceGit.exe to register its Explorer context menu.");

            Install(Registry.CurrentUser, executable);
        }

        internal static void Install(RegistryKey root, string executable)
        {
            foreach (var obj in Objects)
            {
                using var menu = root.CreateSubKey($@"Software\Classes\{obj}\shell\{MenuName}");
                menu.SetValue("", "Open in SourceGit");
                menu.SetValue("Icon", $"\"{executable}\",0");
                menu.SetValue("MultiSelectModel", "Single");

                // Appending \. keeps a drive root's trailing backslash away from the closing quote.
                var argument = obj == @"Directory\Background" ? @"%V\." : obj == "*" ? "%1" : @"%1\.";
                using var command = menu.CreateSubKey("command");
                command.SetValue("", $"\"{executable}\" \"{argument}\"");
            }
        }

        public static void Remove()
        {
            Remove(Registry.CurrentUser);
        }

        internal static void Remove(RegistryKey root)
        {
            foreach (var obj in Objects)
                root.DeleteSubKeyTree($@"Software\Classes\{obj}\shell\{MenuName}", false);
        }
    }
}
