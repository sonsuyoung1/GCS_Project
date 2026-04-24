using GCS_Project.Services;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace GCS_Project
{
    public partial class MainWindow
    {
        #region [지도 초기화]

        // WebView2 기반 지도를 초기화하고 map.html을 로드한다.
        private async Task InitializeMapAsync()
        {
            try
            {
                if (mapWebView.CoreWebView2 != null) return;

                var options = new CoreWebView2EnvironmentOptions();
                var env = await CoreWebView2Environment.CreateAsync(null, null, options);

                await mapWebView.EnsureCoreWebView2Async(env);

                mapWebView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
                _mapController = new MapController(mapWebView);

                mapWebView.NavigationCompleted += MapWebView_NavigationCompleted;
                mapWebView.WebMessageReceived += OnWebMessageReceived;

                LoadMapHtml();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"지도 초기화 실패: {ex.Message}",
                    "오류",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        // 지도 HTML 파일을 WebView2에 로드한다.
        private void LoadMapHtml()
        {
            string mapPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "map.html");

            if (File.Exists(mapPath))
            {
                mapWebView.Source = new Uri(mapPath);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"지도 파일을 찾을 수 없습니다: {mapPath}");
            }
        }

        // 지도 로딩 완료 후 C#과 JavaScript 브릿지를 연결한다.
        private void MapWebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                InitializeMapBridge();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[시스템] 지도 로딩 실패: {e.WebErrorStatus}");
            }
        }

        #endregion


        #region [ViewModel → 지도 브릿지]

        // ViewModel에서 발생한 지도 관련 이벤트를 JavaScript 지도 함수와 연결한다.
        private void InitializeMapBridge()
        {
            if (_viewModel == null || _mapController == null) return;
            if (_mapBridgeInitialized) return;

            _mapBridgeInitialized = true;

            _viewModel.PositionUpdated += async (lat, lng, heading) =>
            {
                if (_mapController == null) return;

                await Dispatcher.InvokeAsync(async () =>
                {
                    await _mapController.UpdateDroneLocation(lat, lng, heading);
                });
            };

            _viewModel.TakeoffPointSet += async (lat, lng) =>
            {
                if (_mapController == null) return;

                await Dispatcher.InvokeAsync(async () =>
                {
                    await _mapController.AddTakeoffMarker(lat, lng);
                });
            };

            _viewModel.UndoWaypointRequested += async () =>
            {
                if (_mapController == null) return;

                await Dispatcher.InvokeAsync(async () =>
                {
                    await _mapController.UndoLastWaypointOnMap();
                });
            };

            _viewModel.ClearWaypointsRequested += async () =>
            {
                if (_mapController == null) return;

                await Dispatcher.InvokeAsync(async () =>
                {
                    await _mapController.ClearAllWaypointsOnMap();
                });
            };

            _viewModel.AllClearRequested += async () =>
            {
                if (_mapController == null) return;

                await Dispatcher.InvokeAsync(async () =>
                {
                    await _mapController.ClearAllOnMap();
                });
            };

            _viewModel.HistoryPathRequested += async (path) =>
            {
                if (_mapController == null) return;

                await Dispatcher.InvokeAsync(async () =>
                {
                    await _mapController.DrawHistoryPath(path);
                });
            };
        }

        #endregion


        #region [지도 → ViewModel 메시지]

        // 지도 JavaScript에서 보낸 메시지를 받아 ViewModel에 전달한다.
        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (_viewModel == null) return;

            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
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
                System.Diagnostics.Debug.WriteLine($"지도 메시지 처리 실패: {ex.Message}");
            }
        }

        #endregion
    }
}