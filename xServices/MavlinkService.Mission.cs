using Asv.IO;
using Asv.Mavlink;
using Asv.Mavlink.Common;
using Asv.Mavlink.Minimal;
using GCS_Project.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GCS_Project.xServices
{
    public partial class MavlinkService
    {
        #region [미션 로직 (Mission & Waypoints)]

        public void StartMission()
        {
            int waypointCount;
            lock (_waypointsLock)
            {
                waypointCount = _waypoints.Count;
            }

            if (_isMissionRunning || waypointCount == 0 || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 임무 시작 불가: 웨이포인트가 없거나 연결 상태를 확인하세요.");
                return;
            }

            _isMissionRunning = true;

            Task.Run(async () =>
            {
                try
                {
                    OnLogReceived?.Invoke($"[미션] 웨이포인트 비행 시작 (남은 지점: {waypointCount}개)");

                    await SetFlightMode(MavModeFlag.MavModeFlagCustomModeEnabled, 4);
                    await Task.Delay(500, _cts?.Token ?? CancellationToken.None);

                    GeoPoint? currentDestination = null;

                    while (_isMissionRunning)
                    {
                        if (_cts != null && _cts.IsCancellationRequested) break;

                        GeoPoint? nextPoint = null;
                        int remainingCount = 0;

                        lock (_waypointsLock)
                        {
                            if (_waypoints.Count > 0)
                            {
                                nextPoint = _waypoints[0];
                                remainingCount = _waypoints.Count;
                            }
                        }

                        if (nextPoint == null) break;

                        if (currentDestination == null ||
                            currentDestination.Lat != nextPoint.Lat ||
                            currentDestination.Lng != nextPoint.Lng)
                        {
                            await GoTo(nextPoint.Lat, nextPoint.Lng, (float)CurrentState.TargetAltitude);
                            currentDestination = nextPoint;

                            OnLogReceived?.Invoke(
                                $"[미션] 다음 웨이포인트로 이동 중... (위도: {Math.Round(nextPoint.Lat, 5)}, 경도: {Math.Round(nextPoint.Lng, 5)})"
                            );
                        }

                        if (IsReached(nextPoint))
                        {
                            lock (_waypointsLock)
                            {
                                if (_waypoints.Count > 0)
                                {
                                    _waypoints.RemoveAt(0);
                                    remainingCount = _waypoints.Count;
                                }
                            }

                            OnWaypointCountChanged?.Invoke(remainingCount);
                            OnLogReceived?.Invoke($"[미션] 웨이포인트 도달 완료. (남은 지점: {remainingCount}개)");

                            currentDestination = null;
                        }

                        await Task.Delay(1000, _cts?.Token ?? CancellationToken.None);
                    }

                    int finalCount;
                    lock (_waypointsLock)
                    {
                        finalCount = _waypoints.Count;
                    }

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
            });
        }

        public async Task GoTo(double lat, double lng, float alt)
        {
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 기체가 연결되지 않아 이동 명령(GoTo)을 수행할 수 없습니다.");
                return;
            }

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            try
            {
                await cmd.SendCommandInt(
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

        private bool IsReached(GeoPoint destination)
        {
            const double R = 6371000.0;

            double lat1 = CurrentState.Latitude * Math.PI / 180.0;
            double lon1 = CurrentState.Longitude * Math.PI / 180.0;
            double lat2 = destination.Lat * Math.PI / 180.0;
            double lon2 = destination.Lng * Math.PI / 180.0;

            double dLat = lat2 - lat1;
            double dLon = lon2 - lon1;

            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1) * Math.Cos(lat2) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            double distanceInMeters = R * c;

            return distanceInMeters <= 3.0;
        }

        public async Task StopMission()
        {
            if (!_isMissionRunning) return;

            _isMissionRunning = false;
            OnLogReceived?.Invoke("[미션] 임무 중단 요청");

            if (CurrentState.IsConnected)
            {
                try
                {
                    await GoTo(CurrentState.Latitude, CurrentState.Longitude, (float)CurrentState.Altitude);
                    OnLogReceived?.Invoke("[명령] 제자리 정지(Hovering) 명령 전송 완료");
                }
                catch (Exception ex)
                {
                    OnLogReceived?.Invoke($"[오류] 정지 명령 실패: {ex.Message}");
                }
            }
        }

        public void AddWaypoint(double lat, double lng)
        {
            if (lat < -90 || lat > 90 || lng < -180 || lng > 180)
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
        #endregion
    }
}