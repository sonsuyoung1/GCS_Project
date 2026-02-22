using System;
using System.IO;
using System.Windows;
using GCS_Project.Models;
using GCS_Project.Services;
using GCS_Project.ViewModels;
using Microsoft.Web.WebView2.Core;
using LibVLCSharp.Shared;


namespace GCS_Project
{
    public partial class MainWindow : Window
    {
        #region [1. 필드 (Fields)]
        private MapController? _mapController;
        private MainViewModel? _viewModel;
        private LibVLC? _libVLC;
        private LibVLCSharp.Shared.MediaPlayer? _mp1; 
        private LibVLCSharp.Shared.MediaPlayer? _mp2;
        private LibVLCSharp.Shared.MediaPlayer? _mp3;
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

            // DataContext가 바뀔 때 ViewModel 참조 업데이트
            this.DataContextChanged += (s, e) =>
            {
                if (this.DataContext is MainViewModel vm) // datecontext가 mainviewmodel인지 확인 하고 맞으면 _viewmodel에 저장.
                    _viewModel = vm;
            };

            this.Loaded += MainWindow_Loaded;
        }
        #endregion

        #region [3. 초기화 (Initialization)]
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _viewModel ??= this.DataContext as MainViewModel;

            if (_viewModel == null)
            {
                MessageBox.Show("ViewModel(DataContext) 연결이 안 됨");
                return;
            }
            try
            {
                await mapWebView.EnsureCoreWebView2Async(null);
                _mapController = new MapController(mapWebView);

                string mapPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "map.html");
                if (File.Exists(mapPath)) mapWebView.Source = new Uri(mapPath);

                InitializeMapBridge();
                mapWebView.WebMessageReceived += OnWebMessageReceived;

                PlayRtpStream(_mp1);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"지도 초기화 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            _viewModel.ChatLog += "\n[UI] MainWindow_Loaded OK";
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
            /*_viewModel.MavlinkService.OnTargetLocationUpdated += (id, lat, lng, type, isTracked) =>
            {
                Dispatcher.Invoke(async () =>
                {
                    await _mapController.UpdateTargetOnMap(id, lat, lng, type, isTracked);
                });
            };*/

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
                _mp1?.Stop(); 
                _mp2?.Stop(); 
                _mp3?.Stop();
                _mp1?.Dispose(); 
                _mp2?.Dispose();
                _mp3?.Dispose();
                _libVLC?.Dispose();
            }
            catch { }
            base.OnClosed(e);
        }
        #endregion
    }
}