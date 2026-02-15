using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Wpf;

namespace GCS_Project.Services
{
    public class MapController
    {
        private readonly WebView2 _webView;

        // 생성자: MainWindow에 있는 WebView2 객체를 전달받습니다.
        public MapController(WebView2 webView)
        {
            _webView = webView;
        }

        // 이륙 지점을 지도에 표시하는 기능
        public async Task AddTakeoffMarker(double lat, double lng)
        {
            if (_webView != null && _webView.CoreWebView2 != null)
            {
                // HTML/JS 쪽에 정의된 addTakeoffMarker(lat, lng) 함수를 호출합니다.
                await _webView.ExecuteScriptAsync($"addTakeoffMarker({lat}, {lng})");
            }
        }

        // 드론의 위도와 경도를 받아 지도의 마커 위치를 갱신.
        public async Task UpdateDroneLocation(double lat, double lng)
        {
            if (_webView != null && _webView.CoreWebView2 != null)
            {
                // HTML 내부에 정의된 자바스크립트 함수 updateLocation(lat, lng)을 호출합니다.
                await _webView.ExecuteScriptAsync($"updateLocation({lat}, {lng})");
            }
        }

        // 지도에서 마지막 찍은 마커와 점선 하나 취소
        public async Task UndoLastWaypointOnMap()
        {
            if (_webView?.CoreWebView2 != null)
            {
                await _webView.ExecuteScriptAsync("undoLastWaypoint()");
            }
        }

        // 지도상의 모든 미션 경로 및 마커 초기화
        public async Task ClearAllWaypointsOnMap()
        {
            if (_webView?.CoreWebView2 != null)
            {
                await _webView.ExecuteScriptAsync("clearAllWaypoints()");
            }
        }

        public async Task UpdateTargetOnMap(int id, double lat, double lng, string type, bool isTracked)
        {
            if (_webView?.CoreWebView2 != null)
            {
                // index.html에 새로 만든 'updateTargetMarker' 함수를 호출합니다.
                // ID, 위도, 경도, 타입, 추적여부를 모두 전달합니다.
                string script = $"updateTargetMarker({id}, {lat}, {lng}, '{type}', {isTracked.ToString().ToLower()})";
                await _webView.ExecuteScriptAsync(script);
            }
        }

        // 지도를 특정 좌표로 즉시 이동시킵니다. (pan이 좌우나 상하로 회전하면서 촬영하는 것을 뜻, 여기서는 지도를 옆으로 미는 동작)
        public async Task PanTo(double lat, double lng)
        {
            if (_webView != null && _webView.CoreWebView2 != null)
                await _webView.ExecuteScriptAsync($"map.panTo(new L.LatLng({lat}, {lng}))");
        }
    }
}
