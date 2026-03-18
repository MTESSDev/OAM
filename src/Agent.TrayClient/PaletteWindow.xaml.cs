// PaletteWindow.xaml.cs
// Fenêtre overlay de recherche (style Command Palette / Spotlight).
// - Hotkey → Show() ; Échap ou perte de focus → Close()
// - Détection auto du type de code par regex
// - Recherche parallèle progressive sur toutes les sources configurées
// - Cache 30 s, timeout 2 s par source
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Agent.TrayClient;

public partial class PaletteWindow : Window
{
    private readonly SearchService _service;
    private readonly ObservableCollection<SearchGroupViewModel> _groups = new();

    private DispatcherTimer? _debounce;
    private CancellationTokenSource _searchCts = new();

    public PaletteWindow(SearchService service)
    {
        _service = service;
        InitializeComponent();
        GroupsPanel.ItemsSource = _groups;

        Loaded      += OnLoaded;
        Deactivated += (_, _) => Close();
    }

    // ── Initialisation ────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Centrer horizontalement, à ~28 % du haut de l'écran de travail
        var wa  = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - ActualWidth)  / 2;
        Top  = wa.Top  +  wa.Height * 0.28;
        SearchBox.Focus();
    }

    // Permettre de déplacer la fenêtre par drag
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragMove();

    // ── Saisie ────────────────────────────────────────────────────────────────

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void SearchBox_TextChanged(object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        var code = SearchBox.Text.Trim();

        // Badge — couleur selon le type détecté
        UpdateTypeBadge(SearchService.DetectType(code));

        // Debounce 300 ms avant de lancer la recherche
        _debounce?.Stop();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += async (_, _) =>
        {
            _debounce!.Stop();
            try { await RunSearchAsync(code); }
            catch { /* silencieux */ }
        };
        _debounce.Start();
    }

    private void UpdateTypeBadge(CodeType type)
    {
        if (type == CodeType.Unknown)
        {
            TypeBadge.Visibility = Visibility.Collapsed;
            return;
        }

        (TypeBadge.Background, TypeLabel.Foreground, TypeLabel.Text) = type switch
        {
            CodeType.CP12    => (Brush(0xE3, 0xF2, 0xFD), Brush(0x15, 0x65, 0xC0), "CP12"),
            CodeType.Dossier => (Brush(0xE8, 0xF5, 0xE9), Brush(0x2E, 0x7D, 0x32), "DOSSIER"),
            CodeType.Requete => (Brush(0xFF, 0xF3, 0xE0), Brush(0xE6, 0x51, 0x00), "REQUÊTE"),
            _                => (Brushes.LightGray, Brushes.Gray, "")
        };
        TypeBadge.Visibility = Visibility.Visible;

        static SolidColorBrush Brush(byte r, byte g, byte b)
            => new(Color.FromRgb(r, g, b));
    }

    // ── Recherche ─────────────────────────────────────────────────────────────

    private async Task RunSearchAsync(string code)
    {
        if (SearchService.DetectType(code) == CodeType.Unknown)
        {
            _groups.Clear();
            EmptyLabel.Visibility = Visibility.Collapsed;
            return;
        }

        // Annuler la recherche précédente
        _searchCts.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        _groups.Clear();
        Spinner.Visibility    = Visibility.Visible;
        EmptyLabel.Visibility = Visibility.Collapsed;

        int count = 0;
        try
        {
            // await foreach résume sur le Dispatcher WPF → _groups.Add est thread-safe
            await foreach (var group in _service.SearchAsync(code, token))
            {
                if (token.IsCancellationRequested) break;
                _groups.Add(SearchGroupViewModel.From(group));
                count++;
            }
        }
        catch (OperationCanceledException) { return; }
        finally
        {
            if (!token.IsCancellationRequested)
                Spinner.Visibility = Visibility.Collapsed;
        }

        EmptyLabel.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Clic sur un résultat ──────────────────────────────────────────────────

    private void Result_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SearchResult result })
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = result.Url,
                    UseShellExecute = true,
                });
            }
            catch { }
            Close();
        }
    }
}
