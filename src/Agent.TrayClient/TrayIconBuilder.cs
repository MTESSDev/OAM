// TrayIconBuilder.cs
// Construit l'icône du tray avec un point de statut (vert = connecté, rouge = déconnecté).
// En mode SIDE_MODE, l'icône de base est teintée en rouge/orange pour distinguer du build prod.
// L'icône de base est chargée depuis la ressource embarquée Resources/tray-base.png.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

internal enum ConnectionStatus { Disconnected, Connected }

internal static class TrayIconBuilder
{
    private static bool    _baseLoaded;
    private static Bitmap? _baseImage;

    // ── API publique ─────────────────────────────────────────────────────────

    /// <summary>
    /// Retourne une icône 32x32 avec la fleur de lys et un point coloré en bas à droite.
    /// </summary>
    public static Task<Icon> BuildAsync(ConnectionStatus status)
    {
        if (!_baseLoaded)
        {
            _baseImage  = LoadEmbeddedBase();
            _baseLoaded = true;
        }

        return Task.FromResult(CreateWithDot(_baseImage, status));
    }

    // ── Chargement de la ressource embarquée ─────────────────────────────────

    private static Bitmap? LoadEmbeddedBase()
    {
        var assembly     = Assembly.GetExecutingAssembly();
        // Nom de ressource : <DefaultNamespace>.<chemin avec . à la place de \>
        // Puisqu'il n'y a pas de namespace racine explicite, le nom est simplement le chemin
        string resourceName = "Agent.TrayClient.Resources.tray-base.png";

        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;

        try   { return new Bitmap(stream); }
        catch { return null; }
    }

    // ── Construction de l'icône avec overlay ─────────────────────────────────

    private static Icon CreateWithDot(Bitmap? baseImg, ConnectionStatus status)
    {
        using var bmp = new Bitmap(32, 32);
        using var g   = Graphics.FromImage(bmp);
        g.SmoothingMode      = SmoothingMode.AntiAlias;
        g.InterpolationMode  = InterpolationMode.HighQualityBicubic;

        g.Clear(Color.Transparent);

        // Icône de base redimensionnée à 32x32
        if (baseImg is not null)
        {
#if SIDE_MODE
            g.DrawImage(TintRed(baseImg), 0, 0, 32, 32);
#else
            g.DrawImage(baseImg, 0, 0, 32, 32);
#endif
        }
        else
            g.DrawIcon(SystemIcons.Application, new Rectangle(0, 0, 32, 32));

        // Point de statut — coin bas-droite
        Color dotColor = status == ConnectionStatus.Connected
            ? Color.FromArgb(0, 200, 83)   // vert
            : Color.FromArgb(229, 57, 53); // rouge

        const int DotSize = 12;
        int x = 32 - DotSize - 1;
        int y = 32 - DotSize - 1;

        // Contour blanc pour la lisibilité
        g.FillEllipse(Brushes.Black, x - 2, y - 2, DotSize + 3, DotSize + 3);
        using var brush = new SolidBrush(dotColor);
        g.FillEllipse(brush, x, y, DotSize, DotSize);

        IntPtr hIcon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        NativeMethods.DestroyIcon(hIcon);
        return icon;
    }

    // ── Teinte rouge (mode Side) ─────────────────────────────────────────────

#if SIDE_MODE
    /// <summary>Applique une teinte rouge à l'image de base pour distinguer visuellement le mode Side.</summary>
    private static Bitmap TintRed(Bitmap src)
    {
        var tinted = new Bitmap(src.Width, src.Height);
        // Matrice de couleur : boost canal rouge, réduction bleu/vert
        float[][] matrix =
        [
            [1.5f, 0.0f, 0.0f, 0.0f, 0.0f],  // R
            [0.0f, 0.4f, 0.0f, 0.0f, 0.0f],  // G
            [0.0f, 0.0f, 0.4f, 0.0f, 0.0f],  // B
            [0.0f, 0.0f, 0.0f, 1.0f, 0.0f],  // A
            [0.0f, 0.0f, 0.0f, 0.0f, 1.0f],
        ];
        var colorMatrix  = new ColorMatrix(matrix);
        var attrs        = new ImageAttributes();
        attrs.SetColorMatrix(colorMatrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);

        using var g = Graphics.FromImage(tinted);
        g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height),
            0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        return tinted;
    }
#endif

    // ── P/Invoke ─────────────────────────────────────────────────────────────

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool DestroyIcon(IntPtr hIcon);
    }
}
