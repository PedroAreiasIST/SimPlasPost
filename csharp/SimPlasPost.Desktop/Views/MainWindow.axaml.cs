using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SimPlasPost.Core.Export;
using SimPlasPost.Core.Models;
using SimPlasPost.Desktop.ViewModels;

namespace SimPlasPost.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Viewport.SetViewModel(_vm);

        // Initial state
        CbField.Items.Clear();
        CbField.Items.Add("None");
        UpdateFieldList();
        UpdateContourNLabel();
        UpdateDefScaleLabel();

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.MeshData))
                UpdateFieldList();
            if (e.PropertyName == nameof(MainViewModel.Log))
                TxtLog.Text = _vm.Log;
        };
    }

    private void UpdateFieldList()
    {
        CbField.Items.Clear();
        CbField.Items.Add("None");
        foreach (var f in _vm.ScalarFields)
            CbField.Items.Add(f);
        CbField.SelectedIndex = _vm.ScalarFields.Length > 0 ? 1 : 0;
        PnlFieldRange.IsVisible = !string.IsNullOrEmpty(_vm.ActiveField);
    }

    // ─── Demo buttons ───
    private void OnDemoClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int idx))
            _vm.ActiveDemo = idx;
    }

    // ─── File loading ───
    private async void OnLoadEnsight(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Ensight Files",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Ensight Files") { Patterns = new[] { "*.case", "*.geo", "*.geom", "*.scl", "*.vec", "*.ens" } },
                    new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } },
                },
            });

            if (files.Count == 0) return;
            var contents = new Dictionary<string, string>();
            foreach (var f in files)
            {
                await using var stream = await f.OpenReadAsync();
                using var reader = new StreamReader(stream);
                contents[f.Name] = await reader.ReadToEndAsync();
            }
            _vm.LoadEnsightFiles(contents);
        }
        catch (Exception ex)
        {
            _vm.Log = $"Error reading files: {ex.Message}";
        }
    }

    private async void OnLoadJson(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open JSON Mesh",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } },
                },
            });

            if (files.Count == 0) return;
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            _vm.LoadJsonMesh(await reader.ReadToEndAsync());
        }
        catch (Exception ex)
        {
            _vm.Log = $"Error reading file: {ex.Message}";
        }
    }

    // ─── Display mode ───
    private void OnDisplayModeChanged(object? sender, RoutedEventArgs e)
    {
        if (RbWireframe.IsChecked == true) _vm.DisplayMode_ = DisplayMode.Wireframe;
        else if (RbPlot.IsChecked == true) _vm.DisplayMode_ = DisplayMode.Plot;
        else if (RbLines.IsChecked == true) _vm.DisplayMode_ = DisplayMode.Lines;

        PnlContourN.IsVisible = _vm.DisplayMode_ == DisplayMode.Lines;
    }

    private void OnContourNChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.ContourN = (int)SlContourN.Value;
            UpdateContourNLabel();
        }
    }

    private void UpdateContourNLabel() => TxtContourN.Text = $"Iso-levels: {(int)SlContourN.Value}";

    // ─── Field selection ───
    private void OnFieldChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CbField.SelectedItem is string s)
        {
            _vm.ActiveField = s == "None" ? "" : s;
            PnlFieldRange.IsVisible = !string.IsNullOrEmpty(_vm.ActiveField);
        }
    }

    private void OnRangeChanged(object? sender, RoutedEventArgs e)
    {
        _vm.UserMin = TxtMin.Text ?? "";
        _vm.UserMax = TxtMax.Text ?? "";
    }

    private void OnRangeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnRangeChanged(sender, e);
    }

    private void OnResetRange(object? sender, RoutedEventArgs e)
    {
        TxtMin.Text = ""; TxtMax.Text = "";
        _vm.ResetRange();
    }

    // ─── Deformation ───
    private void OnShowDefChanged(object? sender, RoutedEventArgs e) =>
        _vm.ShowDef = CbShowDef.IsChecked == true;

    private void OnDefScaleChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.DefScale = SlDefScale.Value;
            UpdateDefScaleLabel();
        }
    }

    private void UpdateDefScaleLabel() => TxtDefScale.Text = $"Scale: {SlDefScale.Value:F1}\u00d7";

    // ─── View presets ───
    private void OnZoomToFit(object? sender, RoutedEventArgs e) =>
        _vm.ZoomToFit(Viewport.Bounds.Width / Math.Max(1, Viewport.Bounds.Height));

    private void OnPresetView(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        var presets = new Dictionary<string, CameraParams>
        {
            ["pX"] = new() { Theta = Math.PI / 2, Phi = Math.PI / 2, Dist = 2.5 },
            ["nX"] = new() { Theta = -Math.PI / 2, Phi = Math.PI / 2, Dist = 2.5 },
            ["pY"] = new() { Theta = 0, Phi = 0.001, Dist = 2.5 },
            ["nY"] = new() { Theta = 0, Phi = Math.PI - 0.001, Dist = 2.5 },
            ["pZ"] = new() { Theta = 0, Phi = Math.PI / 2, Dist = 2.5 },
            ["nZ"] = new() { Theta = Math.PI, Phi = Math.PI / 2, Dist = 2.5 },
            ["iFR"] = new() { Theta = 0.62, Phi = 0.76, Dist = 3.2 },
            ["iFL"] = new() { Theta = -0.62, Phi = 0.76, Dist = 3.2 },
            ["iBR"] = new() { Theta = Math.PI - 0.62, Phi = 0.76, Dist = 3.2 },
            ["iBL"] = new() { Theta = Math.PI + 0.62, Phi = 0.76, Dist = 3.2 },
        };
        if (presets.TryGetValue(tag, out var p)) _vm.SetPresetView(p);
    }

    // ─── Saved views ───
    private void OnSaveView(object? sender, RoutedEventArgs e)
    {
        _vm.ViewName = TxtViewName.Text ?? "";
        _vm.SaveCurrentView();
        TxtViewName.Text = "";
        IcSavedViews.ItemsSource = null;
        IcSavedViews.ItemsSource = _vm.SavedViews;
    }

    private void OnViewNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnSaveView(sender, e);
    }

    private void OnRestoreView(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SavedView v }) _vm.RestoreView(v);
    }

    private void OnDeleteView(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SavedView v })
        {
            _vm.DeleteView(v);
            IcSavedViews.ItemsSource = null;
            IcSavedViews.ItemsSource = _vm.SavedViews;
        }
    }

    // ─── Export ───
    private async void OnExportSvg(object? sender, RoutedEventArgs e) =>
        await SaveExport("SVG", "svg", "image/svg+xml", _vm.ExportSvg());

    private async void OnExportEps(object? sender, RoutedEventArgs e) =>
        await SaveExport("EPS", "eps", "application/postscript", _vm.ExportEps());

    private async void OnExportPdf(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save PDF",
            DefaultExtension = "pdf",
            SuggestedFileName = GetMeshBaseName() + ".pdf",
            FileTypeChoices = new[] { new FilePickerFileType("PDF") { Patterns = new[] { "*.pdf" } } },
        });
        if (file == null) return;
        await using var stream = await file.OpenWriteAsync();
        var bytes = _vm.ExportPdf();
        await stream.WriteAsync(bytes);
    }

    private async Task SaveExport(string title, string ext, string mime, string content)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Save {title}",
            DefaultExtension = ext,
            SuggestedFileName = GetMeshBaseName() + "." + ext,
            FileTypeChoices = new[] { new FilePickerFileType(title) { Patterns = new[] { $"*.{ext}" } } },
        });
        if (file == null) return;
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content);
    }

    private string GetMeshBaseName() =>
        Path.GetFileNameWithoutExtension(_vm.MeshData?.Name ?? "mesh");
}
