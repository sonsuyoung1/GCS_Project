using Microsoft.Web.WebView2.Wpf;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace GCS_Project.Services
{
    public class MapController
    {
        private readonly WebView2 _webView;
        private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;

        public MapController(WebView2 webView)
        {
            _webView = webView;
        }

        #region [1. 드론 및 이륙 지점 표시]
        // 지도에 이륙 지점 마커를 표시한다.
        public async Task AddTakeoffMarker(double lat, double lng)
        {
            string script =
                $"addTakeoffMarker({ToJsNumber(lat)}, {ToJsNumber(lng)})";

            await ExecuteScriptAsync(script);
        }

        // 드론의 현재 위치와 방향을 지도에 반영한다.
        public async Task UpdateDroneLocation(double lat, double lng, double heading)
        {
            string script =
                $"updateLocation({ToJsNumber(lat)}, {ToJsNumber(lng)}, {ToJsNumber(heading)})";

            await ExecuteScriptAsync(script);
        }
        #endregion


        #region [2. 웨이포인트 및 지도 초기화]
        // 지도에서 마지막 웨이포인트 마커와 경로를 삭제한다.
        public async Task UndoLastWaypointOnMap()
        {
            await ExecuteScriptAsync("undoLastWaypoint()");
        }

        // 지도에 표시된 모든 웨이포인트와 미션 경로를 삭제한다.
        public async Task ClearAllWaypointsOnMap()
        {
            await ExecuteScriptAsync("clearAllWaypoints()");
        }

        // 지도에 표시된 모든 마커와 경로 요소를 삭제한다.
        public async Task ClearAllOnMap()
        {
            await ExecuteScriptAsync("clearAllMapElements()");
        }
        #endregion


        #region [3. 타겟 표시]
        // 탐지된 타겟 마커를 지도에 표시하거나 갱신한다.
        public async Task UpdateTargetOnMap(
            int id,
            double lat,
            double lng,
            string type,
            bool isSelected)
        {
            string escapedType = EscapeJsString(type);
            string selectedValue = isSelected.ToString().ToLowerInvariant();

            string script =
                $"updateTargetMarker({id}, {ToJsNumber(lat)}, {ToJsNumber(lng)}, '{escapedType}', {selectedValue})";

            await ExecuteScriptAsync(script);
        }

        // 선택된 타겟 ID를 지도에 전달해 마커 색을 변경한다.
        public async Task SelectTargetMarkerOnMap(int? selectedId)
        {
            string idValue = selectedId.HasValue ? selectedId.Value.ToString() : "null";
            await ExecuteScriptAsync($"selectTargetMarker({idValue})");
        }

        // 사라진 타겟 마커를 지도에서 삭제한다.
        public async Task RemoveTargetMarkerOnMap(int id)
        {
            await ExecuteScriptAsync($"removeTargetMarker({id})");
        }
        #endregion


        #region [4. 지도 이동 및 비행 궤적]
        // 지도를 특정 좌표로 이동시킨다.
        public async Task PanTo(double lat, double lng)
        {
            string script =
                $"map.panTo(new L.LatLng({ToJsNumber(lat)}, {ToJsNumber(lng)}))";

            await ExecuteScriptAsync(script);
        }

        // 비행 기록 좌표 목록을 지도에 선으로 표시한다.
        public async Task DrawHistoryPath(List<Tuple<double, double>> path)
        {
            var jsonPath = JsonSerializer.Serialize(
                path.Select(p => new[] { p.Item1, p.Item2 })
            );

            await ExecuteScriptAsync($"drawFlightPath({jsonPath})");
        }
        #endregion


        #region [5. 공통 유틸리티]
        // WebView2가 준비되어 있으면 JavaScript 코드를 실행한다.
        private async Task ExecuteScriptAsync(string script)
        {
            if (_webView?.CoreWebView2 == null)
                return;

            await _webView.ExecuteScriptAsync(script);
        }

        // double 값을 JavaScript에서 안전하게 읽을 수 있는 문자열로 변환한다.
        private static string ToJsNumber(double value)
        {
            return value.ToString(InvariantCulture);
        }

        // JavaScript 문자열 안에서 문제가 될 수 있는 문자를 이스케이프한다.
        private static string EscapeJsString(string value)
        {
            return value
                .Replace("\\", "\\\\")
                .Replace("'", "\\'");
        }
        #endregion
    }
}