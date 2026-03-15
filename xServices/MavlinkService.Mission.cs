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
        /// <summary>
        /// 등록된 웨이포인트를 순차적으로 방문하는 자율 비행 임무를 시작합니다.
        /// </summary>
        public void StartMission()
        {
            // 1. 사전 검사: 임무 수행 가능 여부 확인
            if (_isMissionRunning || _waypoints.Count == 0 || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 임무 시작 불가: 웨이포인트가 없거나 연결 상태를 확인하세요.");
                return;
            }

            _isMissionRunning = true;

            Task.Run(async () =>
            {
                try
                {
                    OnLogReceived?.Invoke($"[미션] 웨이포인트 비행 시작 (남은 지점: {_waypoints.Count}개)");

                    // 2. 비행 모드 변경: 컴퓨터 명령을 듣는 AUTO(4) 모드로 전환
                    await SetFlightMode(MavModeFlag.MavModeFlagCustomModeEnabled, 4);
                    await Task.Delay(500, _cts?.Token ?? CancellationToken.None);

                    // 현재 드론이 향하고 있는 '지점'을 추적하기 위한 변수
                    GeoPoint? currentDestination = null;

                    // 3. 임무 루프: 리스트에 방문할 점이 남아있는 동안 반복
                    while (_isMissionRunning && _waypoints.Count > 0)
                    {
                        if (_cts != null && _cts.IsCancellationRequested) break;

                        // 리스트의 가장 앞(0번)에 있는 지점을 '이번에 가야 할 곳'으로 설정
                        var nextPoint = _waypoints[0];

                        // 핵심 로직: 새로운 지점으로 목적지가 바뀌었을 때만 GoTo 명령 전송 (중복 전송 방지)
                        if (currentDestination != nextPoint)
                        {
                            await GoTo(nextPoint.Lat, nextPoint.Lng, (float)CurrentState.TargetAltitude);
                            currentDestination = nextPoint;

                            OnLogReceived?.Invoke($"[미션] 다음 웨이포인트로 이동 중... (위도: {Math.Round(nextPoint.Lat, 5)}, 경도: {Math.Round(nextPoint.Lng, 5)})");
                        }

                        // 4. 도착 판정: 드론이 해당 지점의 일정 반경(예: 3m) 안에 들어왔는지 확인
                        if (IsReached(nextPoint))
                        {
                            // 핵심 방어막: UI 관리자를 부르지 않고 lock을 걸어 안전하게 삭제합니다!
                            lock (_waypoints)
                            {
                                if (_waypoints.Count > 0)
                                {
                                    _waypoints.RemoveAt(0); // 도달한 지점은 리스트에서 제거
                                }
                            }

                            // 지운 다음 뷰모델에게 "개수 바뀌었어!"라고 이벤트만 던지면, 
                            // 뷰모델이 알아서 화면(UI)을 갱신해 줍니다!
                            OnWaypointCountChanged?.Invoke(_waypoints.Count);

                            OnLogReceived?.Invoke($"[미션] 웨이포인트 도달 완료. (남은 지점: {_waypoints.Count}개)");
                        }

                        // 1초 간격으로 상태 체크 (통신 과부하 방지)
                        await Task.Delay(1000, _cts?.Token ?? CancellationToken.None);
                    }

                    // 5. 종료 결과 보고
                    if (_waypoints.Count == 0)
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


        /// <summary>
        /// 지정된 위도, 경도, 고도로 기체를 즉시 이동시킵니다.
        /// </summary>
        public async Task GoTo(double lat, double lng, float alt)
        {
            // 1. 안전장치: 드론 객체와 통신 상태 확인
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 기체가 연결되지 않아 이동 명령(GoTo)을 수행할 수 없습니다.");
                return;
            }

            // 2. 명령 하달기(CommandClient) 꺼내기
            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            try
            {
                OnLogReceived?.Invoke($"[명령] 웨이포인트 이동 지시 ({lat:F5}, {lng:F5}, 고도: {alt}m)...");

                // 3. PX4 맞춤형 "해당 좌표로 즉시 이동하라(Reposition)" 명령 전송!
                await cmd.SendCommandInt(
                    MavCmd.MavCmdDoReposition,
                    MavFrame.MavFrameGlobalRelativeAltInt,
                    false,       // 추가됨: current (단발성 명령이므로 false)
                    false,       // 추가됨: autocontinue (자동 다음 명령 실행 여부, false)
                    -1f,         // Param1: 이동 속도 (-1을 넣으면 드론의 기본 속도 사용)
                    0f,          // Param2: 비트마스크 (기본값 0)
                    0f,          // Param3: 예약됨 (0)
                    float.NaN,   // Param4: Yaw (NaN이면 목적지를 바라보며 비행)
                    (int)(lat * 1E7), // X: 위도 (10의 7승 곱하기)
                    (int)(lng * 1E7), // Y: 경도 (10의 7승 곱하기)
                    alt,              // Z: 고도
                    _cts?.Token ?? CancellationToken.None
                );
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] GoTo 명령 전송 실패: {ex.Message}");
            }
        }


        /// <summary>
        /// 드론이 현재 목표 지점(Target)의 허용 범위 내에 도달했는지 판정합니다.
        /// </summary>
        private bool IsReached(GeoPoint destination)
        {
            // 1. 지구의 평균 반지름 (미터 단위)
            const double R = 6371000.0;

            // 2. 도(Degree)를 라디안(Radian)으로 변환
            double lat1 = CurrentState.Latitude * Math.PI / 180.0;
            double lon1 = CurrentState.Longitude * Math.PI / 180.0;
            double lat2 = destination.Lat * Math.PI / 180.0;
            double lon2 = destination.Lng * Math.PI / 180.0;

            // 3. 위도와 경도의 차이
            double dLat = lat2 - lat1;
            double dLon = lon2 - lon1;

            // 4. 하버사인(Haversine) 공식 적용 (곡면 거리 계산)
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1) * Math.Cos(lat2) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

            // 5. 실제 드론과 목적지 사이의 오차 없는 거리 (미터)
            double distanceInMeters = R * c;

            // 6. 오차 범위 3m 이내면 도착으로 판정!
            return distanceInMeters <= 3.0;
        }

        /// <summary>
        /// 진행 중인 자율 비행 임무를 즉시 중단하고 기체를 제자리에 정지시킵니다.
        /// </summary>
        /// <summary>
        /// 진행 중인 자율 비행 임무를 즉시 중단하고 기체를 제자리에 정지(Hovering)시킵니다.
        /// </summary>
        public async Task StopMission()
        {
            // 이미 멈춰있으면 무시
            if (!_isMissionRunning) return;

            // 1. 소프트웨어 미션 루프 중단
            _isMissionRunning = false;

            OnLogReceived?.Invoke("[미션] 임무가 중단되었습니다. 기체를 제동합니다!");

            // 2. 하드웨어(드론) 급브레이크! 
            // 방금 전 완성한 완벽한 GoTo 엔진을 사용해 "너의 지금 위치로 당장 이동해!"라고 명령합니다.
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


        /// <summary>
        /// 지도에 새로운 웨이포인트(경유지) GPS 좌표를 추가합니다.
        /// </summary>
        public void AddWaypoint(double lat, double lng)
        {
            // 1. 방어 코딩: 유효하지 않은 GPS 좌표 필터링
            if (lat < -90 || lat > 90 || lng < -180 || lng > 180)
            {
                OnLogReceived?.Invoke($"[경고] 유효하지 않은 GPS 좌표입니다. (Lat: {lat}, Lng: {lng})");
                return;
            }

            _waypoints.Add(new GeoPoint { Lat = lat, Lng = lng });
            OnWaypointCountChanged?.Invoke(_waypoints.Count);

            // 2. UX 강화: 점이 정상적으로 추가되었음을 로그로 출력
            OnLogReceived?.Invoke($"[미션] 웨이포인트 추가됨 (총 {_waypoints.Count}개)");
        }

        /// <summary>
        /// 실수로 찍은 마지막 웨이포인트를 취소(삭제)합니다.
        /// </summary>
        public void UndoWaypoint()
        {
            if (_waypoints.Count > 0)
            {
                // 리스트의 맨 마지막 요소(가장 최근에 찍은 점)를 제거합니다.
                _waypoints.RemoveAt(_waypoints.Count - 1);

                // UI 화면에 웨이포인트 개수가 줄어들었다고 알려줍니다.
                OnWaypointCountChanged?.Invoke(_waypoints.Count);

                OnLogReceived?.Invoke($"[미션] 마지막 웨이포인트 취소 (남은 개수: {_waypoints.Count}개)");
            }
            else
            {
                OnLogReceived?.Invoke("[경고] 취소할 웨이포인트가 없습니다.");
            }
        }

        /// <summary>
        /// 등록된 모든 웨이포인트를 싹 다 지우고 초기화합니다.
        /// </summary>
        public void ClearWaypoints()
        {
            if (_waypoints.Count > 0)
            {
                // 리스트를 완전히 비웁니다.
                _waypoints.Clear();

                // UI 화면에 개수가 0이 되었다고 알려줍니다.
                OnWaypointCountChanged?.Invoke(0);

                OnLogReceived?.Invoke("[미션] 모든 웨이포인트를 삭제했습니다.");
            }
            else
            {
                // 이미 비어있을 때 누르면 조용히 알려주는 센스
                OnLogReceived?.Invoke("[알림] 이미 웨이포인트가 비어있습니다.");
            }
        }
        #endregion
    }
}
