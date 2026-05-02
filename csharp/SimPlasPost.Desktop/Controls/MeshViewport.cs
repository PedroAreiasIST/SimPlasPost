using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SimPlasPost.Core.Models;
using SimPlasPost.Desktop.ViewModels;

namespace SimPlasPost.Desktop.Controls;

/// <summary>
/// Composite viewport: a GL-backed <see cref="MeshGlSurface"/> renders the
/// 3D mesh; a transparent <see cref="MeshOverlay"/> drawn on top renders the
/// color bar, axis triad, and info string. Pointer input is captured here and
/// dispatched into the view-model's camera (arcball / pan / zoom).
/// </summary>
public class MeshViewport : Panel
{
    private readonly MeshGlSurface _gl = new();
    private readonly MeshOverlay _overlay = new();
    private MainViewModel? _vm;

    private bool _dragging, _panning;
    private Point _lastMouse;

    public MeshViewport()
    {
        // Order matters: GL surface goes in first (bottom layer); overlay
        // is added last so it draws on top.
        Children.Add(_gl);
        Children.Add(_overlay);
        // Pointer events must reach this Panel for the arcball/pan/zoom
        // handlers below — otherwise they get absorbed by OpenGlControlBase
        // or the overlay before bubbling here.
        _gl.IsHitTestVisible = false;
        _overlay.IsHitTestVisible = false;
        // A null Background makes a Panel transparent to hit-testing as
        // well as to painting; with both children non-hit-testable, that
        // would mean no control receives the pointer events at all.
        // Brushes.Transparent paints nothing but DOES count for hit-testing.
        Background = Brushes.Transparent;
        ClipToBounds = true;
        Focusable = true;
    }

    public void SetViewModel(MainViewModel vm)
    {
        _vm = vm;
        _gl.SetViewModel(vm);
        _overlay.SetViewModel(vm);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); if (_vm == null) return;
        _dragging = true; _panning = e.GetCurrentPoint(this).Properties.IsRightButtonPressed;
        _lastMouse = e.GetPosition(this); e.Handled = true; Focus();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); if (!_dragging || _vm == null) return;
        var pos = e.GetPosition(this); var cam = _vm.Camera;
        if (_panning)
        {
            // 1:1 cursor-to-mesh panning.  The orthographic scale used by
            // the projection is `Bounds.Height / (2*Dist)` (height-driven,
            // see Camera.Project), so to get 1 logical pixel of cursor
            // motion → 1 logical pixel of mesh motion the pan-speed factor
            // must be `2*Dist / Bounds.Height` for BOTH x and y deltas.
            //
            // Sign: subtract the delta so the camera target moves
            // OPPOSITE the cursor — a fixed mesh then appears to follow
            // the cursor (drag right → mesh follows right).
            double dx = pos.X - _lastMouse.X, dy = pos.Y - _lastMouse.Y;
            double ps = 2.0 * cam.Dist / Math.Max(1.0, Bounds.Height);
            var r = cam.Rot;
            cam.Tx -= (dx * r[0] - dy * r[3]) * ps;
            cam.Ty -= (dx * r[1] - dy * r[4]) * ps;
            cam.Tz -= (dx * r[2] - dy * r[5]) * ps;
        }
        else
        {
            // Cursor-following rotation: same 1:1 metric as pan.  The
            // orthographic projection scale is height-driven (s = h/(2·Dist)),
            // so θ ≈ 2·dx / h matches the panned object's screen velocity.
            //
            // Drag right → object follows the cursor right (camera tilts left
            // around its Up axis).  Drag down → object follows the cursor down
            // (camera tilts up around its Right axis).  Both deltas have the
            // same screen-frame sign as pan; if either is flipped, the
            // rotation feels inverted relative to a panned object.
            double dx = pos.X - _lastMouse.X, dy = pos.Y - _lastMouse.Y;
            double k = 2.0 / Math.Max(1.0, Bounds.Height);
            var ry = CameraParams.RotAxis(0, 1, 0, -dx * k);
            var rx = CameraParams.RotAxis(1, 0, 0, -dy * k);
            cam.Rot = CameraParams.Mul(CameraParams.Mul(ry, rx), cam.Rot);
        }
        _lastMouse = pos;
        _vm.InvalidateScene();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        _vm?.InvalidateScene();
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e); if (_vm == null) return;
        _vm.Camera.Dist *= e.Delta.Y < 0 ? 1.03 : (1.0 / 1.03);
        _vm.Camera.Dist = Math.Clamp(_vm.Camera.Dist, 0.1, 50);
        _vm.InvalidateScene();
        e.Handled = true;
    }
}
