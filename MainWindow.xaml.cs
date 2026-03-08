using System;
using System.IO;
using System.Windows;
using GCS_Project.Models;
using GCS_Project.Services;
using GCS_Project.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using LibVLCSharp.Shared;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Controls;
using System.ComponentModel;

namespace GCS_Project
{
    public partial class MainWindow : Window
    {
        #region [1. 필드 (Fields)]
        private MapController? _mapController;
        private MainViewModel? _viewModel;
        private LibVLC? _libVLC;
        private LibVLCSharp.Shared.MediaPlayer? _mp1;
        private OverlayWindow? _overlayWindow;

        // ===== Video Frame Size (YOLO 입력 해상도) =====
        private const double FRAME_WIDTH = 1920.0;
        private const double FRAME_HEIGHT = 1080.0;

        private bool _overlayDirty = false;
        private System.Windows.Threading.DispatcherTimer? _overlayTimer;
        #endregion


        #region [2. 생성자 (Constructor)]
        public MainWindow()
        {
            InitializeComponent(); // XAML으로 UI구현한 것을 실제 메모리에 올리고 조립.

            _viewModel = new MainViewModel();
            this.DataContext = _viewModel;

            _viewModel.DetectedTargets.CollectionChanged += (s, e) =>
            {
                _overlayDirty = true;
            };

            _overlayTimer = new System.Windows.Threading.DispatcherTimer();
            _overlayTimer.Interval = TimeSpan.FromMilliseconds(100);
            //나중에 최적화
            _overlayTimer.Tick += (s, e) =>
            {
                RedrawBboxes();
                _overlayDirty = false;
            };
            _overlayTimer.Start();

            // ===== [VIDEO INIT] =====
            Core.Initialize();
            _libVLC = new LibVLC("--network-caching=100");

            // VideoView에 플레이어 연결
            _mp1 = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
            VideoView1.MediaPlayer = _mp1;

            this.LocationChanged += (s, e) =>
            {
                // 1. 인공지평선/나침반 팝업 갱신
                double offset1 = myPopup.HorizontalOffset;
                myPopup.HorizontalOffset = offset1 + 0.01;
                myPopup.HorizontalOffset = offset1;
            };

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


            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateOverlayBounds();
                RedrawBboxes();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
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
                        System.Diagnostics.Debug.WriteLine("[시스템] 지도 로딩 완료 및 브릿지 연결 성공!");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[시스템] 지도 로딩 실패: {args.WebErrorStatus}");
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
                    System.Diagnostics.Debug.WriteLine("지도 파일을 찾을 수 없습니다: " + mapPath);
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

            var topLeft = VideoView1.PointToScreen(new Point(0, 0));

            _overlayWindow.Left = topLeft.X;
            _overlayWindow.Top = topLeft.Y;
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

            // 1. 드론 실시간 위치 업데이트 
            _viewModel.PositionUpdated += (lat, lng, heading) => //03/02
            {
                Dispatcher.InvokeAsync(async () =>
                {
                    if (_mapController != null)
                        await _mapController.UpdateDroneLocation(lat, lng, heading);
                });
            };

            // 2. 이륙 지점 마커 설정
            _viewModel.TakeoffPointSet += (lat, lng) =>
            {
                // UI 스레드에서 실행되도록 Dispatcher로 감싸줍니다.
                Dispatcher.InvokeAsync(async () =>
                {
                    if (_mapController != null)
                    {
                        await _mapController.AddTakeoffMarker(lat, lng);
                    }
                });
            };

            /*// 3. YOLO 다중 타겟 실시간 업데이트 및 이동
            // 서비스의 이벤트를 직접 구독하여 가장 빠른 반응 속도를 보장합니다.
            _viewModel.MavlinkService.OnTargetLocationUpdated += (id, lat, lng, type, isTracked) =>
            {
                Dispatcher.Invoke(async () =>
                {
                    await _mapController.UpdateTargetOnMap(id, lat, lng, type, isTracked);
                });
            };*/

            // 4. 경로(웨이포인트) 관리
            _viewModel.UndoWaypointRequested += async () =>
            {
                // UI 스레드에서 실행되도록 보장
                await Dispatcher.InvokeAsync(async () => {
                    await _mapController.UndoLastWaypointOnMap();
                });
            };

            _viewModel.ClearWaypointsRequested += async () =>
            {
                await Dispatcher.InvokeAsync(async () => {
                    await _mapController.ClearAllWaypointsOnMap();
                    await mapWebView.ExecuteScriptAsync("clearAllMapElements()");
                });
            };
        }
        #endregion


        #region [5. 이벤트 핸들러 (Event Handlers)]
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[JS Bridge Error] {ex.Message}");
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

            base.OnClosed(e);
        }
        //03/08 수정 
        private void OverlayCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RedrawBboxes();
        }

        // 영상 위 클릭하면 좌표 확인 (지금 단계 목표)
        private void OverlayCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_overlayWindow == null || _viewModel == null)
                return;

            var canvas = _overlayWindow.OverlayCanvasControl;
            var p = e.GetPosition(canvas);

            var framePt = CanvasToFrame(p.X, p.Y, canvas.ActualWidth, canvas.ActualHeight);

            if (framePt == null)
            {
                MessageBox.Show($"canvas=({p.X:0.0}, {p.Y:0.0})\nframe=LETTERBOX(무시)");
                return;
            }

            var (fx, fy) = framePt.Value;

            var targets = _viewModel.DetectedTargets;

            if (targets.Count == 0)
            {
                MessageBox.Show($"frame=({fx:0.0}, {fy:0.0})\n탐지된 타겟 없음");
                return;
            }

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
                MessageBox.Show($"frame=({fx:0.0}, {fy:0.0})\n박스 안에 없음");
                return;
            }

            _viewModel.SelectedTarget = hit;

            foreach (var t in targets)
                t.IsBeingTracked = (t == hit);

            RedrawBboxes();

            MessageBox.Show(
                $"SELECTED\nID={hit.ID}\nTYPE={hit.TargetType}\n" +
                $"frame=({fx:0.0}, {fy:0.0})\n" +
                $"bbox(cx={hit.Bx:0.0}, cy={hit.By:0.0}, w={hit.Bw:0.0}, h={hit.Bh:0.0})"
            );
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

            var canvas = _overlayWindow.OverlayCanvasControl;
            canvas.Children.Clear();

            double canvasW = canvas.ActualWidth;
            double canvasH = canvas.ActualHeight;

            if (canvasW <= 1 || canvasH <= 1)
                return;

            foreach (var t in _viewModel.DetectedTargets)
            {
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

                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = rectWidth,
                    Height = rectHeight,
                    StrokeThickness = t.IsBeingTracked ? 3 : 2,
                    Stroke = t.IsBeingTracked
                        ? System.Windows.Media.Brushes.Lime
                        : System.Windows.Media.Brushes.Red,
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
        #endregion
    }
}