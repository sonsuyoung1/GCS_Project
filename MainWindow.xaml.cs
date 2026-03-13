using GCS_Project.Models;
using GCS_Project.Services;
using GCS_Project.ViewModels;
using LibVLCSharp.Shared;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace GCS_Project
{
    public partial class MainWindow : Window
    {
        #region [1. 필드 (Fields)]
        private MapController? _mapController;
        private MainViewModel? _viewModel;
        private LibVLC? _libVLC;
        private LibVLCSharp.Shared.MediaPlayer? _mp1;
        #endregion


        #region [2. 생성자 (Constructor)]
        public MainWindow()
        {
            InitializeComponent(); // XAML으로 UI구현한 것을 실제 메모리에 올리고 조립.

            _viewModel = new MainViewModel();
            this.DataContext = _viewModel;

            // ===== [VIDEO INIT] =====
            Core.Initialize();
            _libVLC = new LibVLC("--network-caching=100");

            // VideoView에 플레이어 연결
            _mp1 = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
            VideoView1.MediaPlayer = _mp1;

            this.Loaded += MainWindow_Loaded;
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

            // 3. 경로(웨이포인트) 관리
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
                });
            };

            _viewModel.AllClearRequested += async () =>
            {
                await Dispatcher.InvokeAsync(async () => {
                    await _mapController.ClearAllOnMap();
                });
            };
            

            /*// 4. YOLO 다중 타겟 실시간 업데이트 및 이동
            // 서비스의 이벤트를 직접 구독하여 가장 빠른 반응 속도를 보장합니다.
            _viewModel.MavlinkService.OnTargetLocationUpdated += (id, lat, lng, type, isTracked) =>
            {
                Dispatcher.Invoke(async () =>
                {
                    await _mapController.UpdateTargetOnMap(id, lat, lng, type, isTracked);
                });
            };*/


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

            string sdpPath = @"C:\Users\Master\OneDrive\바탕 화면\stream.sdp";

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
                _mp1?.Stop(); 
                _mp1?.Dispose(); 
                _libVLC?.Dispose();
            }
            catch { }
            base.OnClosed(e);
        }
        #endregion
    }
}