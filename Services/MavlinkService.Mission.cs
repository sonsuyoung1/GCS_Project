using Asv.IO;
using Asv.Mavlink;
using Asv.Mavlink.Common;
using Asv.Mavlink.Minimal;
using GCS_Project.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GCS_Project.Services
{
    public partial class MavlinkService
    {
        #region [1. 미션 시작/중단]

        // 등록된 웨이포인트를 순서대로 따라가는 미션을 시작한다.
        public void StartMission()
        {
            int waypointCount = GetWaypointCount();

            if (!CanStartMission(waypointCount))
            {
                OnLogReceived?.Invoke("[경고] 임무 시작 불가: 웨이포인트가 없거나 연결 상태를 확인하세요.");
                return;
            }

            _isMissionRunning = true;

            _ = Task.Run(async () => await RunMissionLoop(waypointCount));
        }

        // 미션을 실행하다 그 자리에 바로 스탑
        public async Task StopMission()
        {
            _isMissionRunning = false;
            OnLogReceived?.Invoke("[미션] 임무 중단 요청");

            if (!CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 기체 연결이 없어 미션 정지 명령을 보낼 수 없습니다.");
                return;
            }

            try
            {
                await SetFlightMode(
                    MavModeFlag.MavModeFlagCustomModeEnabled,
                    4,
                    3
                );

                OnLogReceived?.Invoke("[명령] 미션 중단 - AUTO HOLD/LOITER 전환 요청");
            }
            catch (TaskCanceledException)
            {
                OnLogReceived?.Invoke("[경고] 미션 중단 명령이 취소되었습니다.");
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] 미션 중단 실패: {ex.Message}");
            }
        }

        // 미션을 시작할 수 있는 상태인지 확인한다.
        private bool CanStartMission(int waypointCount)
        {
            return !_isMissionRunning &&
                   waypointCount > 0 &&
                   CurrentState.IsConnected;
        }

        // 현재 등록된 웨이포인트 개수를 반환한다.
        private int GetWaypointCount()
        {
            lock (_waypointsLock)
            {
                return _waypoints.Count;
            }
        }

        #endregion


        #region [2. 미션 루프]
        // 미션 루프를 실행하며 웨이포인트를 순서대로 방문한다.
        private async Task RunMissionLoop(int initialWaypointCount)
        {
            try
            {
                OnLogReceived?.Invoke($"[미션] 웨이포인트 비행 시작 (남은 지점: {initialWaypointCount}개)");

                await SetFlightMode(MavModeFlag.MavModeFlagCustomModeEnabled, 4);
                await Task.Delay(500, _cts?.Token ?? CancellationToken.None);

                GeoPoint? currentDestination = null;

                while (_isMissionRunning)
                {
                    if (_cts != null && _cts.IsCancellationRequested)
                        break;

                    GeoPoint? nextPoint = GetNextWaypoint();

                    if (nextPoint == null)
                        break;

                    // 만약에 HOLD 잘되면 이거 삭제하고 다시 실행
                    if (!_isMissionRunning)
                        break;

                    currentDestination = await SendGoToIfDestinationChanged(
                        currentDestination,
                        nextPoint
                    );

                    // 만약에 HOLD 잘되면 이거 삭제하고 다시 실행
                    if (!_isMissionRunning)
                        break;

                    RemoveWaypointIfReached(nextPoint);

                    // 1초 동안 그냥 기다리지 말고 100ms 단위로 쪼개서 Stop 반응성 개선
                    for (int i = 0; i < 10; i++)
                    {
                        if (!_isMissionRunning)
                            break;

                        await Task.Delay(100, _cts?.Token ?? CancellationToken.None);
                    }
                }

                NotifyMissionFinished();
            }
            catch (TaskCanceledException)
            {
                OnLogReceived?.Invoke("[미션] 시스템 종료로 임무가 중단되었습니다.");
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] 미션 루프 에러: {ex.Message}");
            }
            finally
            {
                _isMissionRunning = false;
            }
        }

        // 다음으로 이동할 웨이포인트를 가져온다.
        private GeoPoint? GetNextWaypoint()
        {
            lock (_waypointsLock)
            {
                if (_waypoints.Count == 0)
                    return null;

                return _waypoints[0];
            }
        }

        // 목적지가 바뀐 경우에만 GoTo 명령을 전송한다.
        private async Task<GeoPoint?> SendGoToIfDestinationChanged(
            GeoPoint? currentDestination,
            GeoPoint nextPoint)
        {
            if (currentDestination != null &&
                currentDestination.Lat == nextPoint.Lat &&
                currentDestination.Lng == nextPoint.Lng)
            {
                return currentDestination;
            }

            await GoTo(nextPoint.Lat, nextPoint.Lng, (float)CurrentState.TargetAltitude);

            OnLogReceived?.Invoke(
                $"[미션] 다음 웨이포인트로 이동 중... " +
                $"(위도: {Math.Round(nextPoint.Lat, 5)}, 경도: {Math.Round(nextPoint.Lng, 5)})"
            );

            return nextPoint;
        }

        // 현재 위치가 웨이포인트에 도달했다면 해당 웨이포인트를 제거한다.
        private void RemoveWaypointIfReached(GeoPoint waypoint)
        {
            if (!IsReached(waypoint))
                return;

            int remainingCount;

            lock (_waypointsLock)
            {
                if (_waypoints.Count > 0)
                {
                    _waypoints.RemoveAt(0);
                }

                remainingCount = _waypoints.Count;
            }

            OnWaypointCountChanged?.Invoke(remainingCount);
            OnLogReceived?.Invoke($"[미션] 웨이포인트 도달 완료. (남은 지점: {remainingCount}개)");
        }

        // 미션 종료 시 완료 또는 중단 로그를 남긴다.
        private void NotifyMissionFinished()
        {
            int finalCount = GetWaypointCount();

            if (finalCount == 0)
            {
                OnLogReceived?.Invoke("[미션] 모든 예약된 지점을 방문하였습니다.");
                OnMissionCompleted?.Invoke();
            }
            else
            {
                OnLogReceived?.Invoke("[미션] 사용자에 의해 임무가 중단되었습니다.");
            }
        }
        #endregion


        #region [3. 이동 명령]
        // 지정한 GPS 좌표와 고도로 이동하는 GoTo 명령을 전송한다.
        public async Task GoTo(double lat, double lng, float alt)
        {
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 기체가 연결되지 않아 이동 명령(GoTo)을 수행할 수 없습니다.");
                return;
            }

            var commandClient = _drone.GetMicroservice<ICommandClient>();
            if (commandClient == null) return;

            try
            {
                await commandClient.SendCommandInt(
                    MavCmd.MavCmdDoReposition,
                    MavFrame.MavFrameGlobalRelativeAltInt,
                    false,
                    false,
                    -1f,
                    0f,
                    0f,
                    float.NaN,
                    (int)(lat * 1E7),
                    (int)(lng * 1E7),
                    alt,
                    _cts?.Token ?? CancellationToken.None
                );
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] GoTo 명령 전송 실패: {ex.Message}");
            }
        }

        // 현재 드론 위치가 목표 웨이포인트에 충분히 가까운지 확인한다.
        private bool IsReached(GeoPoint destination)
        {
            const double EarthRadiusMeters = 6371000.0;
            const double ReachThresholdMeters = 3.0;

            double lat1 = CurrentState.Latitude * Math.PI / 180.0;
            double lon1 = CurrentState.Longitude * Math.PI / 180.0;
            double lat2 = destination.Lat * Math.PI / 180.0;
            double lon2 = destination.Lng * Math.PI / 180.0;

            double dLat = lat2 - lat1;
            double dLon = lon2 - lon1;

            double a =
                Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1) * Math.Cos(lat2) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            double distanceInMeters = EarthRadiusMeters * c;

            return distanceInMeters <= ReachThresholdMeters;
        }

        #endregion


        #region [4. 웨이포인트 관리]

        // 지도에서 선택한 좌표를 웨이포인트로 추가한다.
        public void AddWaypoint(double lat, double lng)
        {
            if (!IsValidGpsCoordinate(lat, lng))
            {
                OnLogReceived?.Invoke($"[경고] 유효하지 않은 GPS 좌표입니다. (Lat: {lat}, Lng: {lng})");
                return;
            }

            int count;

            lock (_waypointsLock)
            {
                _waypoints.Add(new GeoPoint { Lat = lat, Lng = lng });
                count = _waypoints.Count;
            }

            OnWaypointCountChanged?.Invoke(count);
            OnLogReceived?.Invoke($"[미션] 웨이포인트 추가됨 (총 {count}개)");
        }

        // 마지막으로 추가한 웨이포인트를 삭제한다.
        public void UndoWaypoint()
        {
            int count;

            lock (_waypointsLock)
            {
                if (_waypoints.Count == 0)
                {
                    OnLogReceived?.Invoke("[경고] 취소할 웨이포인트가 없습니다.");
                    return;
                }

                _waypoints.RemoveAt(_waypoints.Count - 1);
                count = _waypoints.Count;
            }

            OnWaypointCountChanged?.Invoke(count);
            OnLogReceived?.Invoke($"[미션] 마지막 웨이포인트 취소 (남은 개수: {count}개)");
        }

        // 등록된 모든 웨이포인트를 삭제한다.
        public void ClearWaypoints()
        {
            bool hadAny;

            lock (_waypointsLock)
            {
                hadAny = _waypoints.Count > 0;
                _waypoints.Clear();
            }

            OnWaypointCountChanged?.Invoke(0);

            if (hadAny)
            {
                OnLogReceived?.Invoke("[미션] 모든 웨이포인트를 삭제했습니다.");
            }
            else
            {
                OnLogReceived?.Invoke("[알림] 이미 웨이포인트가 비어있습니다.");
            }
        }

        // 위도와 경도 값이 GPS 좌표 범위 안에 있는지 확인한다.
        private bool IsValidGpsCoordinate(double lat, double lng)
        {
            return lat >= -90 &&
                   lat <= 90 &&
                   lng >= -180 &&
                   lng <= 180;
        }

        #endregion
    }
}