using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SimPlasPost.Core.Export;
using SimPlasPost.Core.Models;
using SimPlasPost.Core.Parsers;
using SimPlasPost.Desktop.ViewModels;

namespace SimPlasPost.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        Diag.Log("MainWindow ctor: InitializeComponent...");
        InitializeComponent();
        Diag.Log("MainWindow ctor: InitializeComponent done");
        SizeToScreenFraction(0.6);
        DataContext = _vm;
        Diag.Log("MainWindow ctor: Viewport.SetViewModel...");
        Viewport.SetViewModel(_vm);
        Diag.Log("MainWindow ctor: Viewport.SetViewModel done");

        // Initial state
        CbField.Items.Clear();
        CbField.Items.Add("None");
        UpdateFieldList();
        UpdateContourNLabel();
        UpdateDefScaleLabel();

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.MeshData))
            {
                UpdateFieldList();
                UpdateStepControl();
            }
            if (e.PropertyName == nameof(MainViewModel.Log))
                TxtLog.Text = _vm.Log;
            if (e.PropertyName == nameof(MainViewModel.CurrentStep))
                UpdateStepLabel();
        };

        UpdateStepControl();
    }

    private void UpdateStepControl()
    {
        PnlSteps.IsVisible = _vm.HasSteps;
        int nSteps = Math.Max(1, _vm.StepCount);
        // Avoid firing OnStepChanged while we re-seed the slider for a new mesh.
        _suppressStepChange = true;
        SlStep.Minimum = 0;
        SlStep.Maximum = nSteps - 1;
        SlStep.Value = _vm.CurrentStep;
        _suppressStepChange = false;
        UpdateStepLabel();
    }

    private void UpdateStepLabel() =>
        TxtStep.Text = $"Step: {_vm.CurrentStep + 1} / {Math.Max(1, _vm.StepCount)}";

    private bool _suppressStepChange;

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
            _vm.Log = "Opening file dialog...";

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Ensight Case",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Ensight Case") { Patterns = new[] { "*.case" } },
                    new FilePickerFileType("Ensight Files") { Patterns = new[] { "*.case", "*.geo", "*.geom", "*.scl", "*.vec", "*.ens" } },
                    new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } },
                },
            });

            if (files.Count == 0)
            {
                _vm.Log = "No file selected.";
                return;
            }
            _vm.Log = $"Selected {files.Count} file(s). Resolving case...";

            // Preferred path: a single .case file was selected — pull every file
            // it references straight from disk so the user doesn't have to hand-
            // pick geo/scl/vec siblings.
            if (files.Count == 1 &&
                files[0].Name.EndsWith(".case", StringComparison.OrdinalIgnoreCase))
            {
                string? casePath = GetLocalPath(files[0]);
                _vm.Log = $"Case path: {casePath ?? "<null>"}";
                if (casePath != null && File.Exists(casePath))
                {
                    _vm.Log = $"Reading '{Path.GetFileName(casePath)}' + attachments...";
                    Dictionary<string, string> contents;
                    string? note;
                    try
                    {
                        (contents, note) = await ReadCaseWithAttachmentsAsync(casePath);
                    }
                    catch (Exception rex)
                    {
                        _vm.Log = $"Read error: {rex.Message}";
                        return;
                    }
                    _vm.Log = $"Read {contents.Count} file(s). Parsing mesh...";
                    _vm.LoadEnsightFiles(contents);
                    if (!string.IsNullOrEmpty(note))
                        _vm.Log = $"{_vm.Log}  |  {note}";
                    return;
                }
                _vm.Log = "Could not resolve a local path for the .case file. " +
                          "Please select the case and its sibling files together.";
                return;
            }

            // Fallback: read exactly what the user hand-picked.
            _vm.Log = $"Reading {files.Count} hand-picked file(s)...";
            var bag = new Dictionary<string, string>();
            foreach (var f in files)
            {
                await using var stream = await f.OpenReadAsync();
                using var reader = new StreamReader(stream);
                bag[f.Name] = await reader.ReadToEndAsync();
            }
            _vm.LoadEnsightFiles(bag);
        }
        catch (Exception ex)
        {
            _vm.Log = $"Error reading files: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Resolve the filesystem path of a picked file when it lives on
    /// local disk. Returns null for virtual / non-file backings.</summary>
    private static string? GetLocalPath(IStorageFile f)
    {
        try
        {
            var uri = f.Path;
            if (uri == null) return null;

            // Prefer the absolute "file:/..." path, fall back to the raw string.
            string? p = null;
            if (uri.IsAbsoluteUri)
            {
                try { p = uri.LocalPath; } catch { /* ignore */ }
            }
            if (string.IsNullOrEmpty(p))
            {
                p = uri.ToString();
                const string filePrefix = "file://";
                if (p.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
                    p = Uri.UnescapeDataString(p.Substring(filePrefix.Length));
            }
            return string.IsNullOrEmpty(p) ? null : p;
        }
        catch { return null; }
    }

    /// <summary>
    /// Read a .case file and every file it references (geo + transient variables)
    /// from the same directory. Variable patterns containing '*' are expanded into
    /// every matching file on disk so all time steps are picked up automatically.
    /// Returns the file bag and an optional note (file-count caps, skipped files).
    /// </summary>
    private static async Task<(Dictionary<string, string> Contents, string? Note)> ReadCaseWithAttachmentsAsync(string casePath)
    {
        // Safety caps — Ensight transients can easily hit tens of GB of ASCII,
        // and .NET strings are UTF-16 so on-disk size effectively doubles in RAM.
        // Cap per-variable step count and total bytes read; subsample if needed.
        const int MaxStepsPerVar = 64;
        const long MaxTotalBytes = 512L * 1024 * 1024; // 512 MB of ASCII

        var contents = new Dictionary<string, string>();
        string caseName = Path.GetFileName(casePath);
        string dir = Path.GetDirectoryName(casePath) ?? ".";

        string caseText = await File.ReadAllTextAsync(casePath);
        contents[caseName] = caseText;

        var parsed = EnsightParser.ParseCase(caseText);

        var toLoad = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void AddFile(string name)
        {
            if (seen.Add(name)) toLoad.Add(name);
        }

        if (!string.IsNullOrWhiteSpace(parsed.GeoFile)) AddFile(parsed.GeoFile);

        foreach (var v in parsed.Variables)
        {
            string fn = v.File;
            if (string.IsNullOrWhiteSpace(fn)) continue;

            if (fn.Contains('*'))
            {
                // Ensight convention: '*' stands for a digit of the step index.
                // Treat any run of '*' as "one or more digits" so patterns like
                // "u*" still match "u0001", "u0002", ...
                string escaped = Regex.Escape(fn);
                string pattern = Regex.Replace(escaped, @"(?:\\\*)+", @"\d+");
                var rx = new Regex("^" + pattern + "$", RegexOptions.IgnoreCase);

                var matches = new List<string>();
                foreach (var p in Directory.EnumerateFiles(dir))
                {
                    string name = Path.GetFileName(p);
                    if (rx.IsMatch(name)) matches.Add(name);
                }
                matches.Sort(StringComparer.Ordinal);

                // Subsample evenly across the full range if there are too many,
                // so the user still sees first/last step but we don't OOM.
                if (matches.Count > MaxStepsPerVar)
                {
                    var picked = new List<string>(MaxStepsPerVar);
                    double stride = (matches.Count - 1.0) / (MaxStepsPerVar - 1);
                    for (int k = 0; k < MaxStepsPerVar; k++)
                        picked.Add(matches[(int)Math.Round(k * stride)]);
                    matches = picked.Distinct(StringComparer.Ordinal).ToList();
                }

                foreach (var m in matches) AddFile(m);
            }
            else
            {
                AddFile(fn);
            }
        }

        long totalBytes = 0;
        int skipped = 0;
        foreach (var name in toLoad)
        {
            string full = Path.Combine(dir, name);
            if (!File.Exists(full)) { skipped++; continue; }

            long size;
            try { size = new FileInfo(full).Length; } catch { size = 0; }

            if (totalBytes + size > MaxTotalBytes)
            {
                skipped++;
                continue;
            }

            contents[name] = await File.ReadAllTextAsync(full);
            totalBytes += size;
        }

        string? note = null;
        if (skipped > 0)
            note = $"{skipped} file(s) skipped (caps: {MaxStepsPerVar} steps/var, {MaxTotalBytes / (1024 * 1024)} MB)";

        return (contents, note);
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

    // ─── Time step ───
    private void OnStepChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressStepChange || _vm == null) return;
        _vm.CurrentStep = (int)SlStep.Value;
    }

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
        _vm.ZoomToFit(Viewport.Bounds.Width, Viewport.Bounds.Height);

    private void OnPresetView(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        var presets = new Dictionary<string, CameraParams>
        {
            ["pX"] = CameraParams.FromAngles(Math.PI / 2, Math.PI / 2, 2.5),
            ["nX"] = CameraParams.FromAngles(-Math.PI / 2, Math.PI / 2, 2.5),
            ["pY"] = CameraParams.FromAngles(0, 0.001, 2.5),
            ["nY"] = CameraParams.FromAngles(0, Math.PI - 0.001, 2.5),
            ["pZ"] = CameraParams.FromAngles(0, Math.PI / 2, 2.5),
            ["nZ"] = CameraParams.FromAngles(Math.PI, Math.PI / 2, 2.5),
            ["iFR"] = CameraParams.FromAngles(0.62, 0.76, 3.2),
            ["iFL"] = CameraParams.FromAngles(-0.62, 0.76, 3.2),
            ["iBR"] = CameraParams.FromAngles(Math.PI - 0.62, 0.76, 3.2),
            ["iBL"] = CameraParams.FromAngles(Math.PI + 0.62, 0.76, 3.2),
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
    private async void OnExportPdf(object? sender, RoutedEventArgs e)
    {
        try
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
            // WYSIWYG: PDF page sized to the current viewport so the camera,
            // aspect ratio, and overlay placement on the page match exactly
            // what's on screen.
            int w = Math.Max(100, (int)Viewport.Bounds.Width);
            int h = Math.Max(100, (int)Viewport.Bounds.Height);
            var bytes = _vm.ExportPdf(w, h);
            await stream.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            _vm.Log = $"Export error: {ex.Message}";
        }
    }

    private string GetMeshBaseName() =>
        Path.GetFileNameWithoutExtension(_vm.MeshData?.Name ?? "mesh");

    /// <summary>
    /// Size the window to <paramref name="fraction"/> of the primary screen's
    /// working area (the screen excluding the taskbar/dock), and centre it.
    /// Avalonia's Window.Width/Height are in DIPs, but Screen.WorkingArea is
    /// in physical pixels — divide by Screen.Scaling to convert.
    /// </summary>
    private void SizeToScreenFraction(double fraction)
    {
        var screen = Screens.Primary;
        if (screen == null) return;
        double scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
        Width  = screen.WorkingArea.Width  / scaling * fraction;
        Height = screen.WorkingArea.Height / scaling * fraction;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }
}
