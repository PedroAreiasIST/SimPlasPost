using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Ab4d.SharpEngine.AvaloniaUI;
using Ab4d.SharpEngine.Cameras;
using Ab4d.SharpEngine.Common;
using Ab4d.SharpEngine.Lights;
using Ab4d.SharpEngine.Materials;
using Ab4d.SharpEngine.Meshes;
using Ab4d.SharpEngine.OverlayPanels;
using Ab4d.SharpEngine.SceneNodes;
using SimPlasPost.Core.Colormap;
using SimPlasPost.Core.Geometry;
using SimPlasPost.Core.Models;
using SimPlasPost.Desktop.ViewModels;

namespace SimPlasPost.Desktop.Controls;

/// <summary>
/// GPU-accelerated FE mesh viewport using Ab4d.SharpEngine (Vulkan).
/// Handles millions of elements at 60fps. Built-in orbit/pan/zoom and axis triad.
/// </summary>
public class MeshViewport : Panel
{
    private MainViewModel? _vm;
    private SharpEngineSceneView? _sceneView;
    private TargetPositionCamera? _camera;
    private PointerCameraController? _cameraController;
    private CameraAxisPanel? _axisPanel;

    // Scene nodes (cleared and rebuilt when mesh changes)
    private MeshModelNode? _meshNode;
    private MultiLineNode? _edgesNode;

    // Cache keys
    private MeshData? _cachedMesh;
    private string? _cachedField;
    private bool _cachedShowDef;
    private double _cachedDefScale;
    private DisplayMode _cachedMode;
    private string? _cachedUserMin, _cachedUserMax;

    public void SetViewModel(MainViewModel vm)
    {
        if (_vm != null) _vm.SceneInvalidated -= OnSceneInvalidated;
        _vm = vm;
        _vm.SceneInvalidated += OnSceneInvalidated;

        InitializeSharpEngine();
        RebuildScene();
    }

    private void InitializeSharpEngine()
    {
        if (_sceneView != null) return;

        _sceneView = new SharpEngineSceneView()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            BackgroundColor = Avalonia.Media.Color.FromRgb(255, 255, 255),
        };

        Children.Add(_sceneView);

        // Wait for scene to be ready
        _sceneView.SceneViewInitialized += (sender, args) =>
        {
            SetupScene();
            RebuildScene();
        };
    }

    private void SetupScene()
    {
        if (_sceneView?.Scene == null || _sceneView.SceneView == null) return;

        // Camera: orbit around target
        _camera = new TargetPositionCamera()
        {
            TargetPosition = new Vector3(0, 0, 0),
            Heading = 30,
            Attitude = -20,
            Distance = 4f,
            ShowCameraLight = ShowCameraLightType.Auto,
            ProjectionType = ProjectionTypes.Orthographic,
            ViewWidth = 5f,
        };
        _sceneView.SceneView.Camera = _camera;

        // Lights
        _sceneView.Scene.Lights.Add(new AmbientLight(new Color3(0.3f, 0.3f, 0.3f)));
        _sceneView.Scene.Lights.Add(new DirectionalLight(new Vector3(-0.3f, -1f, -0.3f)));

        // Mouse controls: left-drag = rotate, right-drag = pan, wheel = zoom
        _cameraController = new PointerCameraController(_sceneView)
        {
            RotateCameraConditions = PointerAndKeyboardConditions.LeftPointerButtonPressed,
            MoveCameraConditions = PointerAndKeyboardConditions.RightPointerButtonPressed,
            ZoomMode = CameraZoomMode.ViewCenter,
            PointerWheelDistanceChangeFactor = 1.03f,
        };

        // Axis triad in bottom-left
        _axisPanel = new CameraAxisPanel(_sceneView.SceneView, _camera,
            width: 100, height: 100,
            adjustSizeByDpiScale: true,
            alignment: PositionTypes.BottomLeft);
    }

    private void OnSceneInvalidated()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(RebuildScene);
    }

    private bool NeedsRebuild() => _vm != null && (
        _vm.MeshData != _cachedMesh ||
        _vm.ActiveField != _cachedField ||
        _vm.ShowDef != _cachedShowDef ||
        _vm.DefScale != _cachedDefScale ||
        _vm.DisplayMode_ != _cachedMode ||
        _vm.UserMin != _cachedUserMin ||
        _vm.UserMax != _cachedUserMax);

    private void RebuildScene()
    {
        if (_vm?.MeshData == null || _sceneView?.Scene == null || _camera == null) return;
        if (!NeedsRebuild() && _meshNode != null) return;

        var mesh = _vm.MeshData;
        var dMode = _vm.DisplayMode_;
        _cachedMesh = mesh; _cachedField = _vm.ActiveField;
        _cachedShowDef = _vm.ShowDef; _cachedDefScale = _vm.DefScale;
        _cachedMode = dMode; _cachedUserMin = _vm.UserMin; _cachedUserMax = _vm.UserMax;

        // Remove old nodes
        if (_meshNode != null) { _sceneView.Scene.RootNode.Remove(_meshNode); _meshNode.Dispose(); _meshNode = null; }
        if (_edgesNode != null) { _sceneView.Scene.RootNode.Remove(_edgesNode); _edgesNode.Dispose(); _edgesNode = null; }

        var ns = mesh.Nodes;

        // Bounding box + normalize
        float mnX = float.MaxValue, mnY = float.MaxValue, mnZ = float.MaxValue;
        float mxX = float.MinValue, mxY = float.MinValue, mxZ = float.MinValue;
        for (int i = 0; i < ns.Length; i++)
        {
            var n = ns[i];
            mnX = Math.Min(mnX, (float)n[0]); mxX = Math.Max(mxX, (float)n[0]);
            mnY = Math.Min(mnY, (float)n[1]); mxY = Math.Max(mxY, (float)n[1]);
            mnZ = Math.Min(mnZ, (float)n[2]); mxZ = Math.Max(mxZ, (float)n[2]);
        }
        float cenX = (mnX + mxX) / 2, cenY = (mnY + mxY) / 2, cenZ = (mnZ + mxZ) / 2;
        float span = Math.Max(Math.Max(mxX - mnX, mxY - mnY), Math.Max(mxZ - mnZ, 1e-6f));
        float sc = 2f / span;

        // Displaced positions
        var dispField = mesh.GetDisplacementField();
        var positions = new Vector3[ns.Length];
        float defScale = (float)_vm.DefScale;
        bool showDef = _vm.ShowDef;
        for (int i = 0; i < ns.Length; i++)
        {
            var n = ns[i];
            float dx = 0, dy = 0, dz = 0;
            if (showDef && dispField is { IsVector: true, VectorValues: not null } && i < dispField.VectorValues.Length)
            {
                var d = dispField.VectorValues[i];
                dx = (float)d[0] * defScale; dy = (float)d[1] * defScale; dz = (float)d[2] * defScale;
            }
            positions[i] = new Vector3(
                ((float)n[0] + dx - cenX) * sc,
                ((float)n[1] + dy - cenY) * sc,
                ((float)n[2] + dz - cenZ) * sc);
        }

        // Extract boundary faces
        bool is3D = mesh.Dim == 3 || mesh.Elements.Any(e =>
            FaceTable.Faces.TryGetValue(e.Type, out var ft) && ft.Dim == 3);
        var bfaces = BoundaryExtractor.Extract(mesh.Elements, is3D);

        // Field values for coloring
        double[]? fv = null;
        double fmin = 0, fmax = 1;
        if (dMode != DisplayMode.Wireframe && !string.IsNullOrEmpty(_vm.ActiveField) &&
            mesh.Fields.TryGetValue(_vm.ActiveField, out var field) && !field.IsVector)
        {
            fv = field.ScalarValues;
            if (fv != null && fv.Length > 0)
            {
                fmin = double.MaxValue; fmax = double.MinValue;
                foreach (double v in fv) { fmin = Math.Min(fmin, v); fmax = Math.Max(fmax, v); }
                if (Math.Abs(fmax - fmin) < 1e-15) fmax = fmin + 1;
            }
        }
        double efMin = double.TryParse(_vm.UserMin, out var mn) ? mn : fmin;
        double efMax = double.TryParse(_vm.UserMax, out var mx) ? mx : fmax;
        double efSpan = Math.Abs(efMax - efMin) < 1e-15 ? 1 : efMax - efMin;
        _vm.FRangeMin = fmin; _vm.FRangeMax = fmax;

        // Build triangle mesh: vertices, normals, indices, per-vertex colors
        var vertList = new List<PositionNormalTextureVertex>();
        var indexList = new List<int>();
        var colorList = new List<Color4>();
        var edgePositions = new List<Vector3>();

        foreach (var face in bfaces)
        {
            // Compute face normal
            var p0 = positions[face[0]];
            var p1 = positions[face[1]];
            var p2 = positions[face.Length > 2 ? face[2] : face[1]];
            var normal = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
            if (float.IsNaN(normal.X)) normal = Vector3.UnitY;

            // Triangulate face
            int baseIdx = vertList.Count;
            for (int j = 0; j < face.Length; j++)
            {
                int ni = face[j];
                vertList.Add(new PositionNormalTextureVertex(positions[ni], normal, new System.Numerics.Vector2(0, 0)));

                // Per-vertex color
                Color4 col;
                if (dMode == DisplayMode.Wireframe)
                    col = new Color4(1f, 1f, 1f, 1f);
                else if (fv != null && ni < fv.Length)
                {
                    double t = (fv[ni] - efMin) / efSpan;
                    var (r, g, b) = TurboColormap.Sample(t);
                    col = new Color4((float)r, (float)g, (float)b, 1f);
                }
                else
                    col = new Color4(0.75f, 0.78f, 0.82f, 1f);
                colorList.Add(col);
            }

            // Fan triangulation
            for (int j = 1; j < face.Length - 1; j++)
            {
                indexList.Add(baseIdx);
                indexList.Add(baseIdx + j);
                indexList.Add(baseIdx + j + 1);
            }

            // Wireframe/plot edges
            if (dMode == DisplayMode.Wireframe || dMode == DisplayMode.Plot)
            {
                for (int j = 0; j < face.Length; j++)
                {
                    edgePositions.Add(positions[face[j]]);
                    edgePositions.Add(positions[face[(j + 1) % face.Length]]);
                }
            }
        }

        // Create GPU mesh
        var gpuMesh = new StandardMesh(
            vertList.ToArray(),
            indexList.ToArray(),
            name: "FEMesh");

        var material = new VertexColorMaterial(colorList.ToArray(), name: "FEColors")
        {
            IsTwoSided = true,
        };

        _meshNode = new MeshModelNode(gpuMesh, material, name: "FEModel");
        _sceneView.Scene.RootNode.Add(_meshNode);

        // Wireframe edges
        if (edgePositions.Count > 0)
        {
            _edgesNode = new MultiLineNode(
                edgePositions.ToArray(),
                isLineStrip: false,
                lineColor: new Color4(0.13f, 0.13f, 0.13f, 0.5f),
                lineThickness: 0.5f,
                name: "FEEdges");
            _sceneView.Scene.RootNode.Add(_edgesNode);
        }

        // Auto-fit camera
        _camera.TargetPosition = new Vector3(0, 0, 0);
        _camera.Distance = 4f;
        if (mesh.Dim == 2)
        {
            _camera.Heading = 0;
            _camera.Attitude = -90;
        }
    }
}
