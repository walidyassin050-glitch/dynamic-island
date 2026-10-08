using System;
using System.IO;
using Microsoft.Win32;

namespace MyDynamicIsland
{
    // Adds/removes the app from the current user's "run at logon" list.
    // Uses HKCU\...\CurrentVersion\Run — visible and toggleable in
    // Task Manager > Startup apps, no admin rights needed with the asInvoker
    // manifest.
    //
    // Two identities, one class:
    //  * Plain exe (VS F5 runs, Published\MyDynamicIsland.exe): stores the
    //    quoted exe path, like every normal tray utility.
    //  * MSIX-packaged: the install lives under WindowsApps, whose ACLs refuse
    //    a Run-key launch of the exe path, so we store the documented shell
    //    activation instead: explorer.exe shell:AppsFolder\<FamilyName>!App
    //    (the "!App" half is the Application Id in Package.appxmanifest.src —
    //    keep the two in sync if that ever changes).
    internal static class StartupManager
    {
        private const string RunKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "MyDynamicIsland";

        public static bool IsEnabled()
        {
            string command = DesiredCommand();
            if (command == null) return false;

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                var stored = key?.GetValue(ValueName) as string;
                if (string.IsNullOrWhiteSpace(stored)) return false;

                // Comparing the exact desired command means an entry left by a
                // previous identity (old exe path, or the unpackaged era before
                // MSIX) reads as "not enabled" instead of launching a stale copy.
                return string.Equals(stored.Trim(), command, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                    ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

                string command = DesiredCommand();

                if (enabled && command != null)
                    key.SetValue(ValueName, command, RegistryValueKind.String);
                else if (key.GetValue(ValueName) != null)
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            catch
            {
                // Denied registry access should never take the island down.
            }
        }

        private static string? DesiredCommand()
        {
            // Packaged first: Package.Current only exists (throws otherwise)
            // when the process has identity — the reliable is-MSIX detector,
            // available on every supported Windows build.
            try
            {
                string family = Windows.ApplicationModel.Package.Current.Id.FamilyName;
                if (!string.IsNullOrWhiteSpace(family))
                    return "explorer.exe shell:AppsFolder\\" + family + "!App";
            }
            catch
            {
                // Unpackaged — fall through to the plain path below.
            }

            try
            {
                string? path = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(path)) return null;
                return File.Exists(path) ? "\"" + path + "\"" : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
