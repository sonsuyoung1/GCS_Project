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

        public void DrawTestBox()
        {
            ClearOverlay();

            var rect = new Rectangle
            {
                Width = 200,
                Height = 120,
                Stroke = Brushes.Red,
                StrokeThickness = 3,
                Fill = Brushes.Transparent
            };

            Canvas.SetLeft(rect, 50);
            Canvas.SetTop(rect, 50);
            OverlayCanvas.Children.Add(rect);

            var label = new TextBlock
            {
                Text = "TEST BOX",
                Foreground = Brushes.White,
                Background = Brushes.Red,
                FontSize = 16
            };

            Canvas.SetLeft(label, 50);
            Canvas.SetTop(label, 25);
            OverlayCanvas.Children.Add(label);
        }
    }
}