using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
        Diag.Log("MeshViewport ctor start");
        // Order matters: GL surface goes in first (bottom layer); overlay
        // is added last so it draws on top.
        Children.Add(_gl);
        Children.Add(_overlay);
        ClipToBounds = true;
        Focusable = true;
        Diag.Log("MeshViewport ctor done");
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
            double dx = pos.X - _lastMouse.X, dy = pos.Y - _lastMouse.Y;
            double ps = 2.0 * cam.Dist / Math.Max(Bounds.Width, Bounds.Height);
            var r = cam.Rot;
            cam.Tx += (dx * r[0] - dy * r[3]) * ps;
            cam.Ty += (dx * r[1] - dy * r[4]) * ps;
            cam.Tz += (dx * r[2] - dy * r[5]) * ps;
        }
        else
        {
            double w = Bounds.Width, h = Bounds.Height, dim = Math.Min(w, h);
            cam.Rot = CameraParams.Mul(CameraParams.ArcballDelta(
                (2 * _lastMouse.X - w) / dim, -(2 * _lastMouse.Y - h) / dim,
                (2 * pos.X - w) / dim, -(2 * pos.Y - h) / dim), cam.Rot);
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
