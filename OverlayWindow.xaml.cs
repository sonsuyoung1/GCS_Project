using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GCS_Project
{
    public partial class OverlayWindow : Window
    {
        public OverlayWindow()
        {
            InitializeComponent();
        }
        public Canvas OverlayCanvasControl => OverlayCanvas;
        public void ClearOverlay()
        {
            OverlayCanvas.Children.Clear();
        }
    }
}