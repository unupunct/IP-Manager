using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IPManager.App.ViewModels;

namespace IPManager.App.Views;

public partial class NetworkMapView : UserControl
{
    private MapNodeView? _dragNode;
    private Point _dragOffset;
    private bool _dragging;

    public NetworkMapView() => InitializeComponent();

    private void OnNodeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not NetworkMapViewModel vm || sender is not FrameworkElement element ||
            element.DataContext is not MapNodeView node) return;

        vm.SelectedNode = node;
        if (e.ClickCount >= 2)
        {
            if (vm.OpenDeviceCommand.CanExecute(null)) vm.OpenDeviceCommand.Execute(null);
            e.Handled = true;
            return;
        }
        _dragNode = node;
        _dragOffset = e.GetPosition(element);
        _dragging = true;
        element.CaptureMouse();
        e.Handled = true;
    }

    private void OnNodeMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _dragNode is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag(sender as FrameworkElement);
            return;
        }
        if (DataContext is not NetworkMapViewModel vm) return;
        var position = e.GetPosition(MapCanvas);
        vm.MoveNode(_dragNode, position.X - _dragOffset.X, position.Y - _dragOffset.Y);
        e.Handled = true;
    }

    private void OnNodeMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        EndDrag(sender as FrameworkElement);
        e.Handled = true;
    }

    private void OnNodeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not NetworkMapViewModel vm) return;
        if (vm.OpenDeviceCommand.CanExecute(null)) vm.OpenDeviceCommand.Execute(null);
        e.Handled = true;
    }

    private void EndDrag(FrameworkElement? element)
    {
        _dragging = false;
        _dragNode = null;
        element?.ReleaseMouseCapture();
    }
}
