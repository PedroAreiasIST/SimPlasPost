using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SimPlasPost.Core.Colormap;
using SimPlasPost.Core.Demos;
using SimPlasPost.Core.Export;
using SimPlasPost.Core.Models;
using SimPlasPost.Core.Parsers;
using SimPlasPost.Core.Rendering;

namespace SimPlasPost.Desktop.ViewModels;

public class SavedView
{
    public string Name { get; set; } = "";
    public CameraParams Cam { get; set; } = new();
}

public class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    // ─── State ───
    private MeshData? _meshData;
    public MeshData? MeshData { get => _meshData; set { if (Set(ref _meshData, value)) OnPropertyChanged(nameof(ScalarFields)); } }

    private int _activeDemo;
    public int ActiveDemo { get => _activeDemo; set { if (Set(ref _activeDemo, value)) LoadDemo(value); } }

    private string _activeField = "";
    public string ActiveField { get => _activeField; set { if (Set(ref _activeField, value)) { UserMin = ""; UserMax = ""; InvalidateScene(); } } }

    private DisplayMode _displayMode = DisplayMode.Plot;
    public DisplayMode DisplayMode_ { get => _displayMode; set { if (Set(ref _displayMode, value)) InvalidateScene(); } }

    private bool _showDef;
    public bool ShowDef { get => _showDef; set { if (Set(ref _showDef, value)) InvalidateScene(); } }

    private double _defScale = 1;
    public double DefScale { get => _defScale; set { if (Set(ref _defScale, value)) InvalidateScene(); } }

    private int _contourN = 10;
    public int ContourN { get => _contourN; set { if (Set(ref _contourN, value)) InvalidateScene(); } }

    private string _userMin = "";
    public string UserMin { get => _userMin; set { if (Set(ref _userMin, value)) InvalidateScene(); } }

    private string _userMax = "";
    public string UserMax { get => _userMax; set { if (Set(ref _userMax, value)) InvalidateScene(); } }

    private string _info = "";
    public string Info { get => _info; set => Set(ref _info, value); }

    private string _log = "";
    public string Log { get => _log; set => Set(ref _log, value); }

    private double _fRangeMin;
    public double FRangeMin { get => _fRangeMin; set => Set(ref _fRangeMin, value); }

    private double _fRangeMax = 1;
    public double FRangeMax { get => _fRangeMax; set => Set(ref _fRangeMax, value); }

    public CameraParams Camera { get; } = CameraParams.For3D();

    public ObservableCollection<SavedView> SavedViews { get; } = new();
    private string _viewName = "";
    public string ViewName { get => _viewName; set => Set(ref _viewName, value); }

    /// <summary>Raised when the viewport should redraw.</summary>
    public event Action? SceneInvalidated;

    public string[] ScalarFields => MeshData?.Fields
        .Where(f => !f.Value.IsVector)
        .Select(f => f.Key)
        .ToArray() ?? Array.Empty<string>();

    public double EffMin => double.TryParse(UserMin, out var v) ? v : FRangeMin;
    public double EffMax => double.TryParse(UserMax, out var v) ? v : FRangeMax;

    // ─── Demos ───
    private readonly MeshData[] _demos = DemoMeshGenerator.AllDemos();
    public string[] DemoNames => _demos.Select(d => d.Name.Split('(')[0].Trim()).ToArray();

    public MainViewModel()
    {
        LoadDemo(0);
    }

    private void LoadDemo(int index)
    {
        if (index < 0 || index >= _demos.Length) return;
        LoadMesh(_demos[index]);
    }

    public void LoadMesh(MeshData d)
    {
        MeshData = d;
        var sf = d.Fields.Where(f => !f.Value.IsVector).Select(f => f.Key).ToList();
        ActiveField = sf.FirstOrDefault() ?? "";

        if (d.Dim == 2)
            Camera.CopyFrom(CameraParams.For2D());
        else
            Camera.CopyFrom(CameraParams.For3D());

        Info = $"{d.Name} \u2014 {d.Nodes.Length} nodes, {d.Elements.Count} elems";
        InvalidateScene();
    }

    public void InvalidateScene() => SceneInvalidated?.Invoke();

    /// <summary>Build the projected scene for current state.</summary>
    public ExportScene? BuildScene(int w, int h)
    {
        if (MeshData == null) return null;
        double? eMin = double.TryParse(UserMin, out var mn) ? mn : null;
        double? eMax = double.TryParse(UserMax, out var mx) ? mx : null;

        var scene = SceneBuilder.Build(MeshData, ActiveField, ShowDef, DefScale, Camera, w, h, DisplayMode_, ContourN, eMin, eMax);

        if (scene != null)
        {
            FRangeMin = scene.FMin;
            FRangeMax = scene.FMax;
        }

        return scene;
    }

    // ─── File loading ───
    public void LoadEnsightFiles(Dictionary<string, string> fileContents)
    {
        try
        {
            Log = "Reading files...";
            var mesh = EnsightParser.LoadFromFiles(fileContents);
            LoadMesh(mesh);
            Log = $"{mesh.Nodes.Length} nodes, {mesh.Elements.Count} elems, {mesh.Fields.Count} fields";
        }
        catch (Exception ex)
        {
            Log = $"Error: {ex.Message}";
        }
    }

    public void LoadJsonMesh(string json)
    {
        try
        {
            var d = System.Text.Json.JsonSerializer.Deserialize<JsonMeshDto>(json);
            if (d == null || d.nodes == null || d.elements == null) throw new Exception("Missing nodes or elements");

            var mesh = new MeshData
            {
                Name = "JSON mesh",
                Nodes = d.nodes.Select(n => new[] { n.Length > 0 ? n[0] : 0, n.Length > 1 ? n[1] : 0, n.Length > 2 ? n[2] : 0 }).ToArray(),
                Dim = d.dim ?? (d.nodes.All(n => n.Length < 3 || Math.Abs(n[2]) < 1e-12) ? 2 : 3),
            };

            foreach (var el in d.elements)
            {
                if (el.conn == null) continue;
                if (!Enum.TryParse<ElementType>(el.type, true, out var etype)) continue;
                mesh.Elements.Add(new Element { Type = etype, Conn = el.conn });
            }

            if (d.fields != null)
            {
                foreach (var (name, field) in d.fields)
                {
                    if (field.type == "scalar" && field.values != null)
                        mesh.Fields[name] = new FieldData { Name = name, IsVector = false, ScalarValues = field.values.Select(v => (double)v).ToArray() };
                }
            }

            LoadMesh(mesh);
            Log = "JSON loaded";
        }
        catch (Exception ex)
        {
            Log = $"Error: {ex.Message}";
        }
    }

    // ─── Export ───
    public string ExportSvg(int w = 800, int h = 600)
    {
        var scene = BuildScene(w, h);
        return scene != null ? SvgExporter.Export(scene) : "";
    }

    public string ExportEps(int w = 800, int h = 600)
    {
        var scene = BuildScene(w, h);
        return scene != null ? EpsExporter.Export(scene) : "";
    }

    public byte[] ExportPdf(int w = 800, int h = 600)
    {
        var scene = BuildScene(w, h);
        return scene != null ? PdfExporter.Export(scene) : Array.Empty<byte>();
    }

    // ─── Views ───
    public void SaveCurrentView()
    {
        var name = string.IsNullOrWhiteSpace(ViewName) ? $"View {SavedViews.Count + 1}" : ViewName.Trim();
        SavedViews.Insert(0, new SavedView { Name = name, Cam = Camera.Clone() });
        while (SavedViews.Count > 10) SavedViews.RemoveAt(SavedViews.Count - 1);
        ViewName = "";
    }

    public void RestoreView(SavedView v)
    {
        Camera.CopyFrom(v.Cam);

        InvalidateScene();
    }

    public void DeleteView(SavedView v) => SavedViews.Remove(v);

    public void SetPresetView(CameraParams preset)
    {
        Camera.CopyFrom(preset);

        InvalidateScene();
    }

    public void ResetRange()
    {
        UserMin = "";
        UserMax = "";
    }

    // ─── Zoom to fit ───
    public void ZoomToFit(double aspect)
    {
        if (MeshData == null) return;

        // Compute normalized bounding box
        var ns = MeshData.Nodes;
        double mnX = double.MaxValue, mnY = double.MaxValue, mnZ = double.MaxValue;
        double mxX = double.MinValue, mxY = double.MinValue, mxZ = double.MinValue;
        foreach (var n in ns)
        {
            mnX = Math.Min(mnX, n[0]); mxX = Math.Max(mxX, n[0]);
            mnY = Math.Min(mnY, n[1]); mxY = Math.Max(mxY, n[1]);
            mnZ = Math.Min(mnZ, n[2]); mxZ = Math.Max(mxZ, n[2]);
        }
        double cenX = (mnX + mxX) / 2, cenY = (mnY + mxY) / 2, cenZ = (mnZ + mxZ) / 2;
        double span = Math.Max(Math.Max(mxX - mnX, mxY - mnY), Math.Max(mxZ - mnZ, 1e-12));
        double sc = 2.0 / span;

        double bbMnX = (mnX - cenX) * sc, bbMnY = (mnY - cenY) * sc, bbMnZ = (mnZ - cenZ) * sc;
        double bbMxX = (mxX - cenX) * sc, bbMxY = (mxY - cenY) * sc, bbMxZ = (mxZ - cenZ) * sc;

        var c = Camera;
        var rot = c.Rot;
        // Extract basis from rotation matrix rows
        double[] right = { rot[0], rot[1], rot[2] };
        double[] upC = { rot[3], rot[4], rot[5] };

        double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
        for (int ix = 0; ix < 2; ix++)
        for (int iy = 0; iy < 2; iy++)
        for (int iz = 0; iz < 2; iz++)
        {
            double px = ix == 1 ? bbMxX : bbMnX;
            double py = iy == 1 ? bbMxY : bbMnY;
            double pz = iz == 1 ? bbMxZ : bbMnZ;
            double vx = px * right[0] + py * right[1] + pz * right[2];
            double vy = px * upC[0] + py * upC[1] + pz * upC[2];
            x0 = Math.Min(x0, vx); x1 = Math.Max(x1, vx);
            y0 = Math.Min(y0, vy); y1 = Math.Max(y1, vy);
        }

        double viewW = x1 - x0, viewH = y1 - y0;
        double viewCx = (x0 + x1) / 2, viewCy = (y0 + y1) / 2;
        double cbExtra = !string.IsNullOrEmpty(ActiveField) ? 1.25 : 1.0;

        c.Dist = Math.Max(viewH / 2 * 1.08, Math.Max(viewW / 2 * 1.08 * cbExtra / aspect, 0.3));
        c.Tx = !string.IsNullOrEmpty(ActiveField) ? viewCx - c.Dist * aspect * 0.1 : viewCx;
        c.Ty = viewCy;

        InvalidateScene();
    }
}

// ─── JSON mesh DTOs (internal to avoid polluting public API) ───
internal class JsonMeshDto
{
    public double[][]? nodes { get; set; }
    public JsonElementDto[]? elements { get; set; }
    public int? dim { get; set; }
    public Dictionary<string, JsonFieldDto>? fields { get; set; }
}

internal class JsonElementDto
{
    public string? type { get; set; }
    public int[]? conn { get; set; }
}

internal class JsonFieldDto
{
    public string? type { get; set; }
    public double[]? values { get; set; }
}
