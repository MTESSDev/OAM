#if SIDE_MODE
using System;
using System.Drawing;
using System.Windows.Forms;

/// <summary>
/// Dialogue affiché au démarrage en mode Side quand le build n'est pas à jour.
/// </summary>
internal sealed class SideUpdateForm : Form
{
    internal SideUpdateForm(string envName, string updatePageUrl)
    {
        Text            = $"Agent OAM [{envName}] - Mise a jour requise";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ControlBox      = false;   // retire la croix de fermeture
        StartPosition   = FormStartPosition.CenterScreen;
        TopMost         = true;
        ClientSize      = new Size(420, 120);

        var lbl = new Label
        {
            Text      = $"Une nouvelle version est disponible pour l'environnement {envName}.\n\nVeuillez mettre a jour votre build Side avant de continuer.",
            AutoSize  = false,
            Size      = new Size(400, 60),
            Location  = new Point(10, 12),
        };

        var btnUpdate = new Button
        {
            Text     = "Mettre a jour",
            Size     = new Size(130, 32),
            Location = new Point(10, 76),
        };
        btnUpdate.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = updatePageUrl, UseShellExecute = true }); }
            catch { }
            DialogResult = DialogResult.OK;
            Close();
        };

        Controls.AddRange([lbl, btnUpdate]);
    }
}
#endif
