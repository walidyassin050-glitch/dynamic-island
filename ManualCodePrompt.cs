using System;
using System.Drawing;
using System.Windows.Forms;

namespace MyDynamicIsland
{
    // Only ever opened when Spotify's redirect could not reach the island: a
    // packaged (MSIX) build is refused loopback callbacks by Windows' AppContainer
    // rule unless it is explicitly exempted, so the code the browser was handed
    // dies in the address bar. This takes that URL by hand instead of making you
    // run unpackaged to finish a sign-in you already approved.
    //
    // A code is single-use, the PKCE verifier never leaves this process, and the
    // state must still match — so pasting a stale or borrowed URL cannot do
    // anything except fail the exchange.
    internal sealed class ManualCodePrompt : Form
    {
        private readonly TextBox box = new TextBox();

        public ManualCodePrompt()
        {
            Text = "Finish Spotify sign-in";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(24, 24, 26);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9.5F);
            ClientSize = new Size(466, 168);

            Controls.Add(new Label
            {
                Text = "Copy the whole address from the browser's address bar — the one that " +
                       "won't load, ending in ?code=... — and paste it here.\r\n" +
                       "Don't sign in again first: a code works once.",
                AutoSize = false,
                Size = new Size(422, 58),
                Location = new Point(22, 16),
                ForeColor = Color.FromArgb(190, 190, 198)
            });

            box.BorderStyle = BorderStyle.None;
            box.BackColor = Color.FromArgb(40, 40, 44);
            box.ForeColor = Color.White;
            box.Font = new Font("Consolas", 8.5F);
            box.SetBounds(22, 80, 422, 18);
            Controls.Add(box);

            var ok = new Button
            {
                Text = "Finish",
                DialogResult = DialogResult.OK,
                Location = new Point(282, 124),
                Size = new Size(82, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(56, 56, 62),
                ForeColor = Color.White
            };
            ok.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 96);
            Controls.Add(ok);

            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(368, 124),
                Size = new Size(76, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 40, 44),
                ForeColor = Color.FromArgb(190, 190, 196)
            };
            cancel.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 76);
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        // Read after ShowDialog, not in the constructor.
        public string Pasted => box.Text;
    }
}
