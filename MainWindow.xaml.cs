using GCS_Project.Models;
using GCS_Project.Services;
using GCS_Project.ViewModels;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Windows;


namespace GCS_Project
{
    public partial class MainWindow : Window
    {
        #region [1. 필드 (Fields)]
        private MapController? _mapController;
        private MainViewModel? _viewModel;
        private LibVLC? _libVLC;
        private MediaPlayer? _mp1;
        private MediaPlayer? _mp2;
        private MediaPlayer? _mp3;

<<<<<<< HEAD
        private bool _overlayDirty = false;
        private System.Windows.Threading.DispatcherTimer? _overlayTimer;

        private int? _selectedTargetId = null;
        private bool _mapUpdating = false;
        private bool _mapPending = false;
        private double _pendingMapLat;
        private double _pendingMapLng;
        private double _pendingMapHeading;
        private const int MaxOverlayBoxes = 20;
        private readonly List<System.Windows.Shapes.Rectangle> _bboxRects = new();
        private readonly List<TextBlock> _bboxLabels = new();
        private bool _overlayPoolInitialized = false;
        private System.Windows.Threading.DispatcherTimer? _debugHudTimer;// 디버깅용
=======
>>>>>>> 777f000fd4f2f748845bd5bca5bb3e8dabe1f404
        #endregion

        #region [2. 생성자 (Constructor)]
        public MainWindow()
        {
            InitializeComponent(); // XAML으로 UI구현한 것을 실제 메모리에 올리고 조립.

            // ===== [VIDEO INIT] =====
            Core.Initialize();
            _libVLC = new LibVLC("--network-caching=100");

            // 각 VideoView에 플레이어 연결
            _mp1 = new MediaPlayer(_libVLC);
            _mp2 = new MediaPlayer(_libVLC);
            _mp3 = new MediaPlayer(_libVLC);

            VideoView1.MediaPlayer = _mp1;
            VideoView2.MediaPlayer = _mp2;
            VideoView3.MediaPlayer = _mp3;

            // =========================
            // DataContext가 바뀔 때 ViewModel 참조 업데이트
            this.DataContextChanged += (s, e) =>
            {
                if (this.DataContext is MainViewModel vm) // datecontext가 mainviewmodel인지 확인 하고 맞으면 _viewmodel에 저장.
                    _viewModel = vm;
            };

            this.Loaded += MainWindow_Loaded;
<<<<<<< HEAD

            this.LocationChanged += MainWindow_WindowChanged;
            this.SizeChanged += MainWindow_WindowChanged;
            this.StateChanged += MainWindow_WindowChanged;
            this.Closing += MainWindow_Closing;

            //디버깅용
            _debugHudTimer = new System.Windows.Threading.DispatcherTimer();
            _debugHudTimer.Interval = TimeSpan.FromMilliseconds(100);
            _debugHudTimer.Tick += (s, e) =>
            {
                if (_viewModel != null)
                {
                    var now = DateTime.UtcNow;
                    var processAge = (now - _viewModel.LastProcessUtc).TotalMilliseconds;
                    var applyAge = (now - _viewModel.LastApplyUtc).TotalMilliseconds;

                    this.Title =
                        $"ALT={_viewModel.Drone.Altitude:F2}, " +
                        $"P={processAge:F0}ms, A={applyAge:F0}ms";
                }
            };
            _debugHudTimer.Start();
=======
>>>>>>> 777f000fd4f2f748845bd5bca5bb3e8dabe1f404
        }
        #endregion

        #region [3. 초기화 (Initialization)]
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null)
                return;

<<<<<<< HEAD
            await InitializeMapAsync();

            PlayRtpStream(_mp1);

            _overlayWindow = new OverlayWindow
            {
                Owner = this
            };

            _overlayWindow.Show();

            HookOverlayEvents();
            InitializeOverlayPool();

            UpdateOverlayBounds();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateOverlayBounds();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private async Task InitializeMapAsync()
        {
=======
>>>>>>> 777f000fd4f2f748845bd5bca5bb3e8dabe1f404
            try
            {
                // 1. WebView2 및 MapController 초기화
                await mapWebView.EnsureCoreWebView2Async(null);
                _mapController = new MapController(mapWebView);

                // 2. 지도 HTML 로드
                string mapPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "map.html");
                if (File.Exists(mapPath))
                {
                    mapWebView.Source = new Uri(mapPath);
                }

                // 3. 뷰모델과 지도를 연결 (브릿지 설정)
                InitializeMapBridge();

                // 4. 지도에서 보낸 메시지 수신 설정
                mapWebView.WebMessageReceived += OnWebMessageReceived;

                PlayRtpStream(_mp1);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"지도 초기화 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
<<<<<<< HEAD
        // 아래부터 03/08 추가함
        private void MainWindow_WindowChanged(object? sender, EventArgs e)
        {
            UpdateOverlayBounds();
            _overlayDirty = true;
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (_overlayWindow != null)
            {
                _overlayWindow.Close();
                _overlayWindow = null;
            }
        }

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
                _overlayWindow.Show();

            if (VideoView1.ActualWidth <= 1 || VideoView1.ActualHeight <= 1) return;

            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget == null) return;

            var topLeft = VideoView1.PointToScreen(new Point(0, 0));
            var m = source.CompositionTarget.TransformFromDevice;

            var dipTopLeft = m.Transform(topLeft);

            _overlayWindow.Left = dipTopLeft.X;
            _overlayWindow.Top = dipTopLeft.Y;
            _overlayWindow.Width = VideoView1.ActualWidth;
            _overlayWindow.Height = VideoView1.ActualHeight;
        }
        private void HookOverlayEvents()
        {
            if (_overlayWindow == null) return;

            _overlayWindow.OverlayCanvasControl.SizeChanged += OverlayCanvas_SizeChanged;
            _overlayWindow.OverlayCanvasControl.MouseLeftButtonDown += OverlayCanvas_MouseLeftButtonDown;
        }
        //03/26
        private void InitializeOverlayPool()
        {
            if (_overlayWindow == null) return;
            if (_overlayPoolInitialized) return;

            var canvas = _overlayWindow.OverlayCanvasControl;

            for (int i = 0; i < MaxOverlayBoxes; i++)
            {
                var rect = new System.Windows.Shapes.Rectangle
                {
                    Visibility = Visibility.Collapsed,
                    StrokeThickness = 2,
                    Fill = System.Windows.Media.Brushes.Transparent,
                    IsHitTestVisible = false
                };

                var label = new TextBlock
                {
                    Visibility = Visibility.Collapsed,
                    Foreground = System.Windows.Media.Brushes.Yellow,
                    FontSize = 12,
                    Background = System.Windows.Media.Brushes.Black,
                    IsHitTestVisible = false
                };

                _bboxRects.Add(rect);
                _bboxLabels.Add(label);

                canvas.Children.Add(rect);
                canvas.Children.Add(label);
            }

            _overlayPoolInitialized = true;
        }
=======
>>>>>>> 777f000fd4f2f748845bd5bca5bb3e8dabe1f404
        #endregion

        #region [4. 브릿지: ViewModel -> Map (Event Subscriptions)]
        /// <summary>
        /// ViewModel에서 발생하는 신호를 지도(JS)에 반영하도록 연결합니다.
        /// </summary>
        private void InitializeMapBridge()
        {
            if (_viewModel == null || _mapController == null) return;

            // 1. 드론 실시간 위치 업데이트
            _viewModel.PositionUpdated += (lat, lng) =>
            {
                _ = _mapController.UpdateDroneLocation(lat, lng);
            };

            // 2. 이륙 지점 마커 설정
            _viewModel.TakeoffPointSet += (lat, lng) =>
            {
                _ = _mapController.AddTakeoffMarker(lat, lng);
            };

            // 3. YOLO 다중 타겟 실시간 업데이트 및 이동
            // 서비스의 이벤트를 직접 구독하여 가장 빠른 반응 속도를 보장합니다.
            _viewModel.MavlinkService.OnTargetLocationUpdated += (id, lat, lng, type, isTracked) =>
            {
                Dispatcher.Invoke(async () =>
                {
                    await _mapController.UpdateTargetOnMap(id, lat, lng, type, isTracked);
                });
            };

            // 4. 경로(웨이포인트) 관리
            _viewModel.UndoWaypointRequested += async () =>
                await _mapController.UndoLastWaypointOnMap();

            _viewModel.ClearWaypointsRequested += async () =>
            {
                await _mapController.ClearAllWaypointsOnMap();
                await mapWebView.ExecuteScriptAsync("clearAllMapElements()");
            };
        }
        #endregion

        #region [5. 이벤트 핸들러 (Event Handlers)]
        /// <summary>
        /// 지도(JS)에서 클릭하여 발생한 이벤트를 C# ViewModel로 전달합니다.
        /// </summary>
        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (_viewModel == null)
                return;

            try
            {
                var json = System.Text.Json.JsonDocument.Parse(args.WebMessageAsJson);
                var root = json.RootElement;

                string tag = root.GetProperty("tag").GetString() ?? "";

                if (tag == "ADD_WAYPOINT")
                {
                    double lat = root.GetProperty("lat").GetDouble();
                    double lng = root.GetProperty("lng").GetDouble();
                    _viewModel.AddWaypointFromMap(lat, lng);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[JS Bridge Error] {ex.Message}");
            }
        }
        #endregion

        private void PlayLocalFile(MediaPlayer? mp, string fileName)
        {
            if (mp == null || _libVLC == null) return;

            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);

            if (!File.Exists(path))
            {
                MessageBox.Show($"파일 없음: {path}\n실행 폴더(bin\\...)에 {fileName}이 있어야 함");
                return;
            }

            using var media = new Media(_libVLC, new Uri(path));
            mp.Play(media);
        }

        private void PlayRtpStream(MediaPlayer? mp)
        {
            if (mp == null || _libVLC == null) return;

            string sdpPath = @"C:\Users\LAAA\Desktop\stream.sdp";

            if (!File.Exists(sdpPath))
            {
                MessageBox.Show($"SDP 파일 없음: {sdpPath}");
                return;
            }

            var media = new Media(_libVLC, new Uri(sdpPath));
            mp.Play(media);
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _mp1?.Stop(); _mp2?.Stop(); _mp3?.Stop();
                _mp1?.Dispose(); _mp2?.Dispose(); _mp3?.Dispose();
                _libVLC?.Dispose();
            }
            catch { }
            base.OnClosed(e);
            //디버깅용
            _debugHudTimer?.Stop();
            _debugHudTimer = null;
        }
<<<<<<< HEAD
        //03/08 수정 
        private void OverlayCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            _overlayDirty = true;
        }

        // 영상 위 클릭
        private void OverlayCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_overlayWindow == null || _viewModel == null)
                return;

            var canvas = _overlayWindow.OverlayCanvasControl;
            var p = e.GetPosition(canvas);

            var framePt = CanvasToFrame(p.X, p.Y, canvas.ActualWidth, canvas.ActualHeight);
            if (framePt == null)
                return; // 박스 영역 밖 / letterbox면 아무 반응 없음

            var (fx, fy) = framePt.Value;
            var targets = _viewModel.DetectedTargets;

            if (targets.Count == 0)
                return; // 타겟 없으면 아무 반응 없음

            DetectedTarget? hit = null;
            double bestArea = double.MaxValue;

            foreach (var t in targets)
            {
                double left = t.Bx - (t.Bw / 2.0);
                double right = t.Bx + (t.Bw / 2.0);
                double top = t.By - (t.Bh / 2.0);
                double bottom = t.By + (t.Bh / 2.0);

                if (fx >= left && fx <= right && fy >= top && fy <= bottom)
                {
                    double area = t.Bw * t.Bh;

                    if (area < bestArea)
                    {
                        bestArea = area;
                        hit = t;
                    }
                }
            }

            if (hit == null)
            {
                // 빈 영상 화면 클릭시 선택 초기화
                _selectedTargetId = null;
                _viewModel.SelectedTarget = null;

                RedrawBboxes();
                return;
            }

            _viewModel.SelectedTarget = hit;
            _selectedTargetId = hit.ID;

            RedrawBboxes();
        }

        private (double fx, double fy)? CanvasToFrame(double cx, double cy, double canvasW, double canvasH)
        {
            if (canvasW <= 1 || canvasH <= 1)
                return null;

            double scale = Math.Min(canvasW / FRAME_WIDTH, canvasH / FRAME_HEIGHT);

            double viewW = FRAME_WIDTH * scale;
            double viewH = FRAME_HEIGHT * scale;

            double offsetX = (canvasW - viewW) / 2.0;
            double offsetY = (canvasH - viewH) / 2.0;

            if (cx < offsetX || cx > offsetX + viewW ||
                cy < offsetY || cy > offsetY + viewH)
                return null;

            double fx = (cx - offsetX) / scale;
            double fy = (cy - offsetY) / scale;

            return (fx, fy);
        }

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

        private void RedrawBboxes()
        {
            if (_viewModel == null || _overlayWindow == null)
                return;

            if (!_overlayPoolInitialized)
                return;

            var canvas = _overlayWindow.OverlayCanvasControl;

            double canvasW = canvas.ActualWidth;
            double canvasH = canvas.ActualHeight;

            if (canvasW <= 1 || canvasH <= 1)
                return;

            // 객체 사라지면 선택 초기화
            if (_selectedTargetId.HasValue)
            {
                bool exists = _viewModel.DetectedTargets.Any(t => t.ID == _selectedTargetId.Value);
                if (!exists)
                {
                    _selectedTargetId = null;
                    _viewModel.SelectedTarget = null;
                }
            }

            int drawCount = Math.Min(_viewModel.DetectedTargets.Count, MaxOverlayBoxes);

            // 먼저 전부 숨김
            for (int i = 0; i < MaxOverlayBoxes; i++)
            {
                _bboxRects[i].Visibility = Visibility.Collapsed;
                _bboxLabels[i].Visibility = Visibility.Collapsed;
            }

            for (int i = 0; i < drawCount; i++)
            {
                var t = _viewModel.DetectedTargets[i];

                double leftF = t.Bx - (t.Bw / 2.0);
                double topF = t.By - (t.Bh / 2.0);
                double rightF = t.Bx + (t.Bw / 2.0);
                double bottomF = t.By + (t.Bh / 2.0);

                leftF = Math.Max(0, leftF);
                topF = Math.Max(0, topF);
                rightF = Math.Min(FRAME_WIDTH, rightF);
                bottomF = Math.Min(FRAME_HEIGHT, bottomF);

                if (rightF <= leftF || bottomF <= topF)
                    continue;

                var p1 = FrameToCanvas(leftF, topF, canvasW, canvasH);
                var p2 = FrameToCanvas(rightF, bottomF, canvasW, canvasH);

                double rectLeft = p1.cx;
                double rectTop = p1.cy;
                double rectWidth = p2.cx - p1.cx;
                double rectHeight = p2.cy - p1.cy;

                if (rectWidth <= 1 || rectHeight <= 1)
                    continue;

                bool selected = (_selectedTargetId.HasValue && t.ID == _selectedTargetId.Value);

                var rect = _bboxRects[i];
                rect.Width = rectWidth;
                rect.Height = rectHeight;
                rect.StrokeThickness = selected ? 3 : 2;
                rect.Stroke = selected
                    ? System.Windows.Media.Brushes.Red
                    : System.Windows.Media.Brushes.Lime;

                Canvas.SetLeft(rect, rectLeft);
                Canvas.SetTop(rect, rectTop);
                rect.Visibility = Visibility.Visible;

                var label = _bboxLabels[i];
                label.Text = $"ID:{t.ID} {t.TargetType}";
                Canvas.SetLeft(label, rectLeft);
                Canvas.SetTop(label, Math.Max(0, rectTop - 20));
                label.Visibility = Visibility.Visible;
            }
        }
        //ai 엔터 커멘드 03/16 추가
        private void AiCommandTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            if (_viewModel == null)
                return;

            if (_viewModel.SendAICommand.CanExecute(null))
            {
                _viewModel.SendAICommand.Execute(null);
            }

            e.Handled = true;
        }
        #endregion
=======

>>>>>>> 777f000fd4f2f748845bd5bca5bb3e8dabe1f404
    }
}