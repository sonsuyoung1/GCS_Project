using System;
using System.IO;
using System.Linq;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using GCS_Project.Models;
using GCS_Project.Services;
using GCS_Project.ViewModels;

using LibVLCSharp.Shared;
using Microsoft.Web.WebView2.Core;

namespace GCS_Project
{
    public partial class MainWindow : Window
    {
        #region [1. 필드 (Fields)]
        private MapController? _mapController;
        private ViewModel? _viewModel;
        private LibVLC? _libVLC;
        private LibVLCSharp.Shared.MediaPlayer? _mp1;
        private OverlayWindow? _overlayWindow;
        private bool _mapBridgeInitialized = false;
        // ===== Video Frame Size (YOLO 입력 해상도) =====
        private const double FRAME_WIDTH = 640.0;
        private const double FRAME_HEIGHT = 480.0;
        private bool _overlayDirty = false;
        private System.Windows.Threading.DispatcherTimer? _overlayTimer;

        private int? _selectedTargetId = null;
        #endregion


        #region [2. 생성자 (Constructor)]
        public MainWindow()
        {
            InitializeComponent(); // XAML으로 UI구현한 것을 실제 메모리에 올리고 조립.

            _viewModel = new ViewModel();
            this.DataContext = _viewModel;

            _viewModel.DetectedTargets.CollectionChanged += DetectedTargets_CollectionChanged;
            foreach (var t in _viewModel.DetectedTargets)
            {
                t.PropertyChanged += DetectedTarget_PropertyChanged;
            }
            // ===== [VIDEO INIT] =====
            Core.Initialize();
            _libVLC = new LibVLC("--network-caching=0");

            // VideoView에 플레이어 연결
            _mp1 = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
            VideoView1.MediaPlayer = _mp1;

            this.Loaded += MainWindow_Loaded;

            this.LocationChanged += MainWindow_WindowChanged;
            this.SizeChanged += MainWindow_WindowChanged;
            this.StateChanged += MainWindow_WindowChanged;
            this.Closing += MainWindow_Closing;

        }
        #endregion


        #region [3. 초기화 (Initialization)]
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null)
            {
                MessageBox.Show("ViewModel(DataContext) 연결이 안 됨");
                return;
            }

            await InitializeMapAsync();

            PlayRtpStream(_mp1);

            _overlayWindow = new OverlayWindow
            {
                Owner = this
            };

            _overlayWindow.Show();

            HookOverlayEvents();

            UpdateOverlayBounds();

            _overlayTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };

            /*_overlayTimer.Tick += (s, args) =>
            {
                if (_overlayWindow == null || _viewModel == null)
                    return;

                if (!_overlayDirty)
                    return;

                _overlayDirty = false;
                UpdateOverlayBounds();
                RedrawBboxes();
            };*/
            //위에 주석 풀고 아래 삭제해야돼 04/12
            _overlayTimer.Tick += (s, args) =>
            {
                if (_overlayWindow == null || _viewModel == null)
                    return;

                _viewModel.FlushDelayedTargets();
                SmoothTargets();

                if (!_overlayDirty)
                    return;

                _overlayDirty = false;
                UpdateOverlayBounds();
                RedrawBboxes();
            };
            //여기 위에까지 삭제
            _overlayTimer.Start();

            await Dispatcher.InvokeAsync(
                UpdateOverlayBounds,
                System.Windows.Threading.DispatcherPriority.Loaded
            );
        }

        private async Task InitializeMapAsync()
        {
            try
            {
                // 1. 중복 초기화 방지 체크
                if (mapWebView.CoreWebView2 != null) return;

                // 2. 환경 설정 (한 번만 수행)
                // 사용자 데이터 폴더를 명시적으로 지정하면 안정성이 올라갑니다 (null 대신 경로 지정 가능)
                var options = new CoreWebView2EnvironmentOptions();
                var env = await CoreWebView2Environment.CreateAsync(null, null, options);

                // 3. 비동기 초기화 시작
                await mapWebView.EnsureCoreWebView2Async(env);

                // 4. 초기화 성공 후 설정 (이 순서가 매우 중요합니다!)
                // 배경 투명 설정
                mapWebView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
                _mapController = new MapController(mapWebView);

                // 5. 지도가 완전히 로드되었을 때 이벤트를 연결하도록 타이밍 변경!
                mapWebView.NavigationCompleted += (s, args) =>
                {
                    if (args.IsSuccess)
                    {
                        // 지도가 100% 켜졌을 때만 C#과 JS 브릿지 연결
                        InitializeMapBridge();
                        //System.Diagnostics.Debug.WriteLine("[시스템] 지도 로딩 완료 및 브릿지 연결 성공!");
                    }
                    else
                    {
                        //System.Diagnostics.Debug.WriteLine($"[시스템] 지도 로딩 실패: {args.WebErrorStatus}");
                    }
                };

                // 6. 브릿지 메시지 설정 (이건 밖에서 미리 등록해도 괜찮습니다)
                mapWebView.WebMessageReceived += OnWebMessageReceived;

                // 7. 지도 HTML 로드 명령 (마지막에 실행!)
                string mapPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "map.html");
                if (File.Exists(mapPath))
                {
                    mapWebView.Source = new Uri(mapPath);
                }
                else
                {
                    //System.Diagnostics.Debug.WriteLine("지도 파일을 찾을 수 없습니다: " + mapPath);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"지도 초기화 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        // 아래부터 03/08 추가함
        private void MainWindow_WindowChanged(object? sender, EventArgs e)
        {
            UpdateOverlayBounds();
            RedrawBboxes();
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
        #endregion

        
        #region [4. 브릿지: ViewModel -> Map (Event Subscriptions)]
        /// <summary>
        /// ViewModel에서 발생하는 신호를 지도(JS)에 반영하도록 연결합니다.
        /// </summary>
        private void InitializeMapBridge()
        {
            if (_viewModel == null || _mapController == null) return;
            if (_mapBridgeInitialized) return;

            _mapBridgeInitialized = true;

            // 1. 드론 실시간 위치 업데이트 (single-flight)
            _viewModel.PositionUpdated += (lat, lng, heading) =>
            {
                Dispatcher.InvokeAsync(async () =>
                {
                    if (_mapController != null)
                    {
                        await _mapController.UpdateDroneLocation(lat, lng, heading);
                    }
                });
            };

            // 2. 이륙 지점 마커 설정
            _viewModel.TakeoffPointSet += (lat, lng) =>
            {
                Dispatcher.InvokeAsync(async () =>
                {
                    if (_mapController != null)
                    {
                        await _mapController.AddTakeoffMarker(lat, lng);
                    }
                });
            };

            // 3. 경로(웨이포인트) 관리
            _viewModel.UndoWaypointRequested += async () =>
            {
                await Dispatcher.InvokeAsync(async () =>
                {
                    await _mapController.UndoLastWaypointOnMap();
                });
            };

            _viewModel.ClearWaypointsRequested += async () =>
            {
                await Dispatcher.InvokeAsync(async () =>
                {
                    if (_mapController != null)
                    {
                        await _mapController.ClearAllWaypointsOnMap();
                    }
                });
            };

            _viewModel.AllClearRequested += async () =>
            {
                await Dispatcher.InvokeAsync(async () =>
                {
                    await _mapController.ClearAllOnMap();
                    await mapWebView.ExecuteScriptAsync("clearAllMapElements()");
                });
            };

            _viewModel.HistoryPathRequested += (path) =>
            {
                Dispatcher.InvokeAsync(async () =>
                {
                    if (_mapController != null)
                    {
                        await _mapController.DrawHistoryPath(path);
                    }
                });
            };
        }
        #endregion


        #region [ 5. 수동 조종 (Manual Control) 변수 및 이벤트]

        // 현재 조이스틱 상태를 기억할 변수 (Z:500은 제자리 고도 유지, 나머지는 0이 정지)
        private short _joyPitch = 0;   // W, S
        private short _joyRoll = 0;    // A, D
        private short _joyThrust = 500;// Up, Down
        private short _joyYaw = 0;     // Left, Right

        // 1. 수동 조종 모드 켜기 (버튼 눌림)
        private void ManualModeToggle_Checked(object sender, RoutedEventArgs e)
        {
            _viewModel?.ToggleManualControl(true);
        }

        // 2. 수동 조종 모드 끄기 (버튼 해제)
        private void ManualModeToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            _viewModel?.ToggleManualControl(false);
        }

        // 3. 키보드를 눌렀을 때 (가속 페달 밟기)
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // 꿀팁: 꾹 누르고 있을 때 이벤트가 수십 번 중복 발생하는 것을 막아줍니다!
            if (e.IsRepeat) return;

            bool isChanged = false;

            switch (e.Key)
            {
                case Key.W: _joyPitch = 1000; isChanged = true; break;  // 전진
                case Key.S: _joyPitch = -1000; isChanged = true; break; // 후진
                case Key.A: _joyRoll = -1000; isChanged = true; break;  // 좌측 이동
                case Key.D: _joyRoll = 1000; isChanged = true; break;   // 우측 이동

                case Key.Up: _joyThrust = 1000; isChanged = true; break;  // 상승
                case Key.Down: _joyThrust = 0; isChanged = true; break;   // 하강

                case Key.Left: _joyYaw = -1000; isChanged = true; break;  // 좌회전
                case Key.Right: _joyYaw = 1000; isChanged = true; break;  // 우회전
            }

            // 값이 바뀌었으면 뷰모델로 전송!
            if (isChanged)
            {
                _viewModel?.UpdateJoystick(_joyPitch, _joyRoll, _joyThrust, _joyYaw);
            }
        }

        // 4. 키보드에서 손을 뗐을 때 (브레이크 밟기 - 제자리 복귀)
        private void Window_KeyUp(object sender, KeyEventArgs e)
        {
            bool isChanged = false;

            switch (e.Key)
            {
                case Key.W:
                case Key.S: _joyPitch = 0; isChanged = true; break; // 전후진 멈춤

                case Key.A:
                case Key.D: _joyRoll = 0; isChanged = true; break;  // 좌우 멈춤

                case Key.Up:
                case Key.Down: _joyThrust = 500; isChanged = true; break; // 고도 유지 (Hovering)

                case Key.Left:
                case Key.Right: _joyYaw = 0; isChanged = true; break; // 회전 멈춤
            }

            if (isChanged)
            {
                _viewModel?.UpdateJoystick(_joyPitch, _joyRoll, _joyThrust, _joyYaw);
            }
        }
        #endregion


        #region [6. 이벤트 핸들러 (Event Handlers)]
        //각 타겟 속성 변경 감지
        private void DetectedTarget_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DetectedTarget.Bx) ||
                e.PropertyName == nameof(DetectedTarget.By) ||
                e.PropertyName == nameof(DetectedTarget.Bw) ||
                e.PropertyName == nameof(DetectedTarget.Bh) ||
                e.PropertyName == nameof(DetectedTarget.DisplayBx) ||
                e.PropertyName == nameof(DetectedTarget.DisplayBy) ||
                e.PropertyName == nameof(DetectedTarget.DisplayBw) ||
                e.PropertyName == nameof(DetectedTarget.DisplayBh) ||
                e.PropertyName == nameof(DetectedTarget.TargetType) ||
                e.PropertyName == nameof(DetectedTarget.IsBeingTracked))
            {
                _overlayDirty = true;
            }
        }
        //컬렉션 추가/삭제 시 PropertyChanged 연결/해제
        private void DetectedTargets_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems)
                {
                    if (item is DetectedTarget t)
                    {
                        t.PropertyChanged += DetectedTarget_PropertyChanged;
                    }
                }
            }

            if (e.OldItems != null)
            {
                foreach (var item in e.OldItems)
                {
                    if (item is DetectedTarget t)
                    {
                        t.PropertyChanged -= DetectedTarget_PropertyChanged;
                    }
                }
            }
            _overlayDirty = true;

        }

        /// <summary>
        /// 지도(JS)에서 클릭하여 발생한 이벤트를 C# ViewModel로 전달합니다.
        /// </summary>
        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
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
            catch (Exception)
            {
            }
        }

        private void PlayLocalFile(MediaPlayer? mp, string fileName)
        {
            if (mp == null || _libVLC == null) return;

            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);

            if (!File.Exists(path))
            {
                MessageBox.Show($"파일 없음: {path}\n실행 폴더(bin\\...)에 {fileName}이 있어야 함");
                return;
            }

            using var media = new LibVLCSharp.Shared.Media(_libVLC, new Uri(path));
            mp.Play(media);
        }

        private void PlayRtpStream(MediaPlayer? mp)
        {
            if (mp == null || _libVLC == null) return;

            string sdpPath = @"C:\Users\82106\OneDrive\바탕 화면\stream.sdp";
            //string sdpPath = @"C:\Users\LAAA\Desktop\stream.sdp";
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
                _overlayTimer?.Stop();
                _overlayTimer = null;

                _overlayWindow?.Close();
                _overlayWindow = null;

                _viewModel?.Dispose();

                _mp1?.Stop();
                _mp1?.Dispose();
                _libVLC?.Dispose();
            }
            catch { }

            if (_viewModel != null)
            {
                _viewModel.DetectedTargets.CollectionChanged -= DetectedTargets_CollectionChanged;

                foreach (var t in _viewModel.DetectedTargets)
                {
                    t.PropertyChanged -= DetectedTarget_PropertyChanged;
                }
            }

            base.OnClosed(e);
        }
        //03/08 수정 
        private void OverlayCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RedrawBboxes();
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

        //스무딩 작업
        private void SmoothTargets()
        {
            if (_viewModel == null)
                return;

            const double alpha = 0.25; // 시작값

            bool changed = false;

            foreach (var t in _viewModel.DetectedTargets)
            {
                double nextBx = t.DisplayBx + (t.Bx - t.DisplayBx) * alpha;
                double nextBy = t.DisplayBy + (t.By - t.DisplayBy) * alpha;
                double nextBw = t.DisplayBw + (t.Bw - t.DisplayBw) * alpha;
                double nextBh = t.DisplayBh + (t.Bh - t.DisplayBh) * alpha;

                if (Math.Abs(nextBx - t.DisplayBx) > 0.01)
                {
                    t.DisplayBx = nextBx;
                    changed = true;
                }

                if (Math.Abs(nextBy - t.DisplayBy) > 0.01)
                {
                    t.DisplayBy = nextBy;
                    changed = true;
                }

                if (Math.Abs(nextBw - t.DisplayBw) > 0.01)
                {
                    t.DisplayBw = nextBw;
                    changed = true;
                }

                if (Math.Abs(nextBh - t.DisplayBh) > 0.01)
                {
                    t.DisplayBh = nextBh;
                    changed = true;
                }
            }

            if (changed)
                _overlayDirty = true;
        }

        private void RedrawBboxes()
        {
            if (_viewModel == null || _overlayWindow == null)
                return;

            var canvas = _overlayWindow.OverlayCanvasControl;
            canvas.Children.Clear();

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

            foreach (var t in _viewModel.DetectedTargets)
            {
                double leftF = t.DisplayBx - (t.DisplayBw / 2.0);
                double topF = t.DisplayBy - (t.DisplayBh / 2.0);
                double rightF = t.DisplayBx + (t.DisplayBw / 2.0);
                double bottomF = t.DisplayBy + (t.DisplayBh / 2.0);

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

                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = rectWidth,
                    Height = rectHeight,
                    StrokeThickness = selected ? 3 : 2,
                    Stroke = selected
                        ? System.Windows.Media.Brushes.Red
                        : System.Windows.Media.Brushes.Lime,
                    Fill = System.Windows.Media.Brushes.Transparent
                };

                Canvas.SetLeft(rect, rectLeft);
                Canvas.SetTop(rect, rectTop);
                canvas.Children.Add(rect);

                var label = new TextBlock
                {
                    Text = $"ID:{t.ID} {t.TargetType}",
                    Foreground = System.Windows.Media.Brushes.Yellow,
                    FontSize = 12,
                    Background = System.Windows.Media.Brushes.Black
                };

                Canvas.SetLeft(label, rectLeft);
                Canvas.SetTop(label, Math.Max(0, rectTop - 20));
                canvas.Children.Add(label);
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
    }
}