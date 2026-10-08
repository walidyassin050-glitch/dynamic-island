using System;
using System.Windows.Forms;

namespace MyDynamicIsland
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            ApplicationConfiguration.Initialize();

            using var island = new Form1();

            // The tray's "Open settings" / double-click raise SettingsRequested
            // on the UI thread — this is what was missing: the event existed
            // but nobody ever listened, so clicking it did nothing.
            island.SettingsRequested += () =>
            {
                using var dlg = new SettingsForm(
                    island.Settings, island.NotificationAccessText, island.SpotifyStatusText);
                if (dlg.ShowDialog(island) == DialogResult.OK)
                    island.ApplyNewSettings(dlg.Collect());
            };

            Application.Run(island);
        }
    }
}
