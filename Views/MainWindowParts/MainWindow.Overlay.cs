using GCS_Project.Models;
using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GCS_Project
{
    public partial class MainWindow
    {
        #region [오버레이 초기화]

        // 영상 위에 YOLO 박스를 그릴 오버레이 창을 초기화한다.
        private void InitializeOverlayWindow()
        {
            _overlayWindow = new OverlayWindow
            {
                Owner = this
            };

            _overlayWindow.Show();

            HookOverlayEvents();
            UpdateOverlayBounds();
        }

        // 오버레이를 주기적으로 다시 그리는 타이머를 초기화한다.
        private void InitializeOverlayTimer()
        {
            _overlayTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };

            _overlayTimer.Tick += (s, e) =>
            {
                if (_overlayWindow == null || _viewModel == null) return;
                if (!_overlayDirty) return;

                _overlayDirty = false;

                UpdateOverlayBounds();
                RedrawBboxes();
            };

            _overlayTimer.Start();
        }

        // 오버레이 창의 캔버스 이벤트를 연결한다.
        private void HookOverlayEvents()
        {
            if (_overlayWindow == null) return;

            _overlayWindow.OverlayCanvasControl.SizeChanged += OverlayCanvas_SizeChanged;
            _overlayWindow.OverlayCanvasControl.MouseLeftButtonDown += OverlayCanvas_MouseLeftButtonDown;
        }

        // 비디오 영역 위에 오버레이 창을 정확히 맞춘다.
        private void UpdateOverlayBounds()
        {
            if (_overlayWindow == null) return;
            if (!IsLoaded) return;

            if (WindowState == WindowState.Minimized)
            {
                _overlayWindow.Hide();
                return;
            }

            if (!_overlayWindow.IsVisible)
            {
                _overlayWindow.Show();
            }

            if (VideoView1.ActualWidth <= 1 || VideoView1.ActualHeight <= 1) return;

            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget == null) return;

            Point topLeft = VideoView1.PointToScreen(new Point(0, 0));
            var transform = source.CompositionTarget.TransformFromDevice;
            Point dipTopLeft = transform.Transform(topLeft);

            _overlayWindow.Left = dipTopLeft.X;
            _overlayWindow.Top = dipTopLeft.Y;
            _overlayWindow.Width = VideoView1.ActualWidth;
            _overlayWindow.Height = VideoView1.ActualHeight;
        }

        #endregion


        #region [타겟 변경 감지]

        // 탐지 타겟 속성이 변경되면 오버레이를 다시 그리도록 표시한다.
        private void DetectedTarget_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (IsOverlayProperty(e.PropertyName))
            {
                _overlayDirty = true;
            }
        }

        // 탐지 타겟이 추가/삭제되면 이벤트 연결을 갱신한다.
        private void DetectedTargets_CollectionChanged(
            object? sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems)
                {
                    if (item is DetectedTarget target)
                    {
                        target.PropertyChanged += DetectedTarget_PropertyChanged;
                    }
                }
            }

            if (e.OldItems != null)
            {
                foreach (var item in e.OldItems)
                {
                    if (item is DetectedTarget target)
                    {
                        target.PropertyChanged -= DetectedTarget_PropertyChanged;
                    }
                }
            }

            _overlayDirty = true;
        }

        // 오버레이 박스에 영향을 주는 속성인지 확인한다.
        private bool IsOverlayProperty(string? propertyName)
        {
            return propertyName == nameof(DetectedTarget.Bx) ||
                   propertyName == nameof(DetectedTarget.By) ||
                   propertyName == nameof(DetectedTarget.Bw) ||
                   propertyName == nameof(DetectedTarget.Bh) ||
                   propertyName == nameof(DetectedTarget.TargetType) ||
                   propertyName == nameof(DetectedTarget.IsBeingTracked);
        }

        #endregion


        #region [타겟 선택]

        // 오버레이 캔버스 크기가 바뀌면 박스를 다시 그린다.
        private void OverlayCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RedrawBboxes();
        }

        // 영상 위를 클릭했을 때 해당 위치의 타겟을 선택한다.
        private void OverlayCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_overlayWindow == null || _viewModel == null) return;

            var canvas = _overlayWindow.OverlayCanvasControl;
            Point clickPoint = e.GetPosition(canvas);

            var framePoint = CanvasToFrame(
                clickPoint.X,
                clickPoint.Y,
                canvas.ActualWidth,
                canvas.ActualHeight
            );

            if (framePoint == null) return;

            var (fx, fy) = framePoint.Value;
            DetectedTarget? hitTarget = FindTargetAtFramePoint(fx, fy);

            if (hitTarget == null)
            {
                ClearSelectedTarget();
                return;
            }

            SelectTarget(hitTarget);
        }

        // 프레임 좌표에 해당하는 타겟을 찾는다.
        private DetectedTarget? FindTargetAtFramePoint(double fx, double fy)
        {
            if (_viewModel == null) return null;

            DetectedTarget? hit = null;
            double bestArea = double.MaxValue;

            foreach (var target in _viewModel.DetectedTargets)
            {
                double left = target.Bx - (target.Bw / 2.0);
                double right = target.Bx + (target.Bw / 2.0);
                double top = target.By - (target.Bh / 2.0);
                double bottom = target.By + (target.Bh / 2.0);

                bool containsPoint =
                    fx >= left &&
                    fx <= right &&
                    fy >= top &&
                    fy <= bottom;

                if (!containsPoint) continue;

                double area = target.Bw * target.Bh;

                if (area < bestArea)
                {
                    bestArea = area;
                    hit = target;
                }
            }

            return hit;
        }

        // 현재 선택된 타겟을 초기화한다.
        private void ClearSelectedTarget()
        {
            if (_viewModel == null) return;

            _selectedTargetId = null;
            _viewModel.SelectedTarget = null;

            RedrawBboxes();
        }

        // 클릭된 타겟을 현재 선택 타겟으로 설정한다.
        private void SelectTarget(DetectedTarget target)
        {
            if (_viewModel == null) return;

            _viewModel.SelectedTarget = target;
            _selectedTargetId = target.ID;

            RedrawBboxes();
        }

        #endregion


        #region [좌표 변환]

        // 영상 캔버스 좌표를 YOLO 입력 프레임 좌표로 변환한다.
        private (double fx, double fy)? CanvasToFrame(double cx, double cy, double canvasW, double canvasH)
        {
            if (canvasW <= 1 || canvasH <= 1) return null;

            double scale = Math.Min(canvasW / FRAME_WIDTH, canvasH / FRAME_HEIGHT);

            double viewW = FRAME_WIDTH * scale;
            double viewH = FRAME_HEIGHT * scale;

            double offsetX = (canvasW - viewW) / 2.0;
            double offsetY = (canvasH - viewH) / 2.0;

            if (cx < offsetX ||
                cx > offsetX + viewW ||
                cy < offsetY ||
                cy > offsetY + viewH)
            {
                return null;
            }

            double fx = (cx - offsetX) / scale;
            double fy = (cy - offsetY) / scale;

            return (fx, fy);
        }

        // YOLO 입력 프레임 좌표를 영상 캔버스 좌표로 변환한다.
        private (double cx, double cy) FrameToCanvas(double fx, double fy, double canvasW, double canvasH)
        {
            double scale = Math.Min(canvasW / FRAME_WIDTH, canvasH / FRAME_HEIGHT);

            double viewW = FRAME_WIDTH * scale;
            double viewH = FRAME_HEIGHT * scale;

            double offsetX = (canvasW - viewW) / 2.0;
            double offsetY = (canvasH - viewH) / 2.0;

            double cx = offsetX + (fx * scale);
            double cy = offsetY + (fy * scale);

            return (cx, cy);
        }

        #endregion


        #region [BBox 그리기]

        // 현재 탐지 타겟 목록을 기준으로 영상 위에 BBox를 다시 그린다.
        private void RedrawBboxes()
        {
            if (_viewModel == null || _overlayWindow == null) return;

            Canvas canvas = _overlayWindow.OverlayCanvasControl;
            canvas.Children.Clear();

            double canvasW = canvas.ActualWidth;
            double canvasH = canvas.ActualHeight;

            if (canvasW <= 1 || canvasH <= 1) return;

            ClearSelectedTargetIfMissing();

            foreach (var target in _viewModel.DetectedTargets)
            {
                DrawTargetBox(canvas, target, canvasW, canvasH);
            }
        }

        // 선택된 타겟이 목록에서 사라졌으면 선택을 해제한다.
        private void ClearSelectedTargetIfMissing()
        {
            if (_viewModel == null) return;
            if (!_selectedTargetId.HasValue) return;

            bool exists = _viewModel.DetectedTargets.Any(t => t.ID == _selectedTargetId.Value);

            if (!exists)
            {
                _selectedTargetId = null;
                _viewModel.SelectedTarget = null;
            }
        }

        // 하나의 탐지 타겟 박스와 라벨을 캔버스에 그린다.
        private void DrawTargetBox(Canvas canvas, DetectedTarget target, double canvasW, double canvasH)
        {
            double leftF = target.Bx - (target.Bw / 2.0);
            double topF = target.By - (target.Bh / 2.0);
            double rightF = target.Bx + (target.Bw / 2.0);
            double bottomF = target.By + (target.Bh / 2.0);

            leftF = Math.Max(0, leftF);
            topF = Math.Max(0, topF);
            rightF = Math.Min(FRAME_WIDTH, rightF);
            bottomF = Math.Min(FRAME_HEIGHT, bottomF);

            if (rightF <= leftF || bottomF <= topF) return;

            var topLeft = FrameToCanvas(leftF, topF, canvasW, canvasH);
            var bottomRight = FrameToCanvas(rightF, bottomF, canvasW, canvasH);

            double rectLeft = topLeft.cx;
            double rectTop = topLeft.cy;
            double rectWidth = bottomRight.cx - topLeft.cx;
            double rectHeight = bottomRight.cy - topLeft.cy;

            if (rectWidth <= 1 || rectHeight <= 1) return;

            bool isSelected =
                _selectedTargetId.HasValue &&
                target.ID == _selectedTargetId.Value;

            DrawRectangle(canvas, rectLeft, rectTop, rectWidth, rectHeight, isSelected);
            DrawTargetLabel(canvas, target, rectLeft, rectTop);
        }

        // 탐지 타겟의 사각형 박스를 그린다.
        private void DrawRectangle(
            Canvas canvas,
            double left,
            double top,
            double width,
            double height,
            bool isSelected)
        {
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = width,
                Height = height,
                StrokeThickness = isSelected ? 3 : 2,
                Stroke = isSelected
                    ? System.Windows.Media.Brushes.Red
                    : System.Windows.Media.Brushes.Lime,
                Fill = System.Windows.Media.Brushes.Transparent
            };

            Canvas.SetLeft(rect, left);
            Canvas.SetTop(rect, top);
            canvas.Children.Add(rect);
        }

        // 탐지 타겟의 ID와 종류 라벨을 그린다.
        private void DrawTargetLabel(Canvas canvas, DetectedTarget target, double left, double top)
        {
            var label = new TextBlock
            {
                Text = $"ID:{target.ID} {target.TargetType}",
                Foreground = System.Windows.Media.Brushes.Yellow,
                FontSize = 12,
                Background = System.Windows.Media.Brushes.Black
            };

            Canvas.SetLeft(label, left);
            Canvas.SetTop(label, Math.Max(0, top - 20));
            canvas.Children.Add(label);
        }

        #endregion
    }
}