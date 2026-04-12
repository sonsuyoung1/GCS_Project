using Asv.Mavlink.Minimal;
using GCS_Project.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using GCS_Project.Data;

namespace GCS_Project.ViewModels
{
    public partial class ViewModel
    {
        private DateTime _lastDbSaveTime = DateTime.MinValue;

        #region [이벤트 핸들러 (Event Handlers)]
        private async void InitializeService()
        {
            if (_mavStarting) return;
            _mavStarting = true;

            try
            {
                // 2. 인자는 깔끔하게 비웁니다! (서비스의 기본값 사용)
                await _mavlinkService.StartAsync();
                ChatLog += "\n[시스템] MAVLink 연결 성공";
            }
            catch (Exception ex)
            {
                // 3. 에러 방패 (포트가 이미 사용 중이거나 와이파이가 끊겼을 때 프로그램이 안 뻗음)
                ChatLog += $"\n[오류] MAVLink 시작 실패: {ex.Message}";
            }
            finally
            {
                // 4. 문지기 퇴근
                _mavStarting = false;
            }

            // 드론의 데이터 업데이트
            _mavlinkService.OnDataUpdated += (state) =>
            {
                Application.Current.Dispatcher.BeginInvoke(() => HandleDroneData(state));
                NotifyCommandStates();
            };

            // 웨이포인트 개수 변화
            _mavlinkService.OnWaypointCountChanged += (count) =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _currentWaypointCount = count;
                    NotifyCommandStates();
                    ChatLog += $"\n[시스템] 웨이포인트 개수 동기화 됨: {count}개"; // 확인용 로그
                });
            };

            _mavlinkService.OnMissionCompleted += () =>
            {
                Application.Current.Dispatcher.BeginInvoke(() => {
                    ChatLog += "\n모든 목적지에 도착하여 미션을 종료합니다.";
                });
            };

            _mavlinkService.OnLogReceived += (message) =>
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    ChatLog += $"\n{message}";
                });
            };

            // MavlinkService가 드론 모드 변경을 감지하면 내 FlightMode를 업데이트!
            _mavlinkService.OnFlightModeChanged += (newMode) =>
            {
                // UI 스레드에서 안전하게 업데이트 (WPF 특성상 Dispatcher 필요할 수 있음)
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if(_mavlinkService._isMissionRunning)
                    {
                        FlightMode = "MISSION";
                    }
                    else
                    {
                        FlightMode = newMode; // SetProperty가 실행되면서 UI와 버튼 상태가 자동 갱신됨
                    }
                    ChatLog += $"\n[시스템] 기체 비행 모드 변경 확인: {newMode}";
                });
            };

            // 타겟 업데이트
            /*_mavlinkService.OnTargetLocationUpdated += (id, lat, lng, type, isTracked) =>
            {
                Application.Current.Dispatcher.BeginInvoke(async () =>
                {
                    // 1. 지도에 마커 표시 (기존 유지)
                    PersonDetectedOnMap?.Invoke(lat, lng);

                    if (id == SelectedTarget?.ID)
                    {
                        _targetLostTimer.Stop(); // 타이머 리셋
                        _targetLostTimer.Start();
                    }

                    // 2. 현재 추적 모드이고, 이 타겟이 내가 선택한 타겟이라면?
                    if (FlightMode == "TRACKING" && SelectedTarget != null && SelectedTarget.ID == id)
                    {
                        // 드론에게 실시간 좌표 전송!
                        await _mavlinkService.GoTo(lat, lng);
                    }

                    _ = Task.Run(() =>
                    {
                        try
                        {
                            using (var db = new AppDbContext())
                            {
                                var newTargetLog = new TargetLog
                                {
                                    Timestamp = DateTime.Now,
                                    TargetType = type, // "Tank", "Vehicle" 등 YOLO가 보내준 타입
                                    Latitude = lat,
                                    Longitude = lng,
                                    IsStriked = false  // 방금 발견한 거니까 아직 타격 전(false)으로 저장!
                                };

                                db.TargetLogs.Add(newTargetLog);
                                db.SaveChanges(); // DB에 타겟 발견 기록 쾅!
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[DB 오류] 타겟 저장 실패: {ex.Message}");
                        }
                    });
                });
            };*/
        }

        private void HandleDroneData(DroneState state)
        {
            if (!Drone.IsConnected) Drone.IsConnected = true;
            if (state.Latitude == 0 && state.Longitude == 0) return;

            // 기존 위치 업데이트 로직
            if ((DateTime.UtcNow - _lastMapUpdate).TotalMilliseconds > 200)
            {
                _lastMapUpdate = DateTime.UtcNow;

                // 1. 드론 현재 위치 업데이트
                PositionUpdated?.Invoke(state.Latitude, state.Longitude, state.Yaw);

                // 2. 이륙 마커 업데이트 (스위치를 확인해서 딱 한 번만 그린다. )
                if (state.TakeoffLat.HasValue && state.TakeoffLng.HasValue && !_isTakeoffMarkerDrawn)
                {
                    TakeoffPointSet?.Invoke(state.TakeoffLat.Value, state.TakeoffLng.Value);
                    _isTakeoffMarkerDrawn = true; // "나 그렸어! 이제 다시 부르지 마!"
                }
            }

            // DB 저장 로직
            if ((DateTime.UtcNow - _lastDbSaveTime).TotalMilliseconds > 1000)
            {
                _lastDbSaveTime = DateTime.UtcNow;

                // Task.Run으로 감싸서 DB 저장 때문에 화면(UI)이 끊기는 걸 막아줍니다!
                Task.Run(() =>
                {
                    try
                    {
                        using (var db = new AppDbContext()) // 다리 연결
                        {
                            var newLog = new FlightLog
                            {
                                Timestamp = DateTime.Now,
                                Latitude = state.Latitude,
                                Longitude = state.Longitude,
                                Altitude = state.Altitude,
                                Speed = state.Speed,
                                BatteryPercent = state.BatteryPercent,
                                FlightMode = this.FlightMode // 현재 상태 (TAKEOFF, MISSION 등)
                            };

                            db.FlightLogs.Add(newLog); // 상자에 넣고
                            db.SaveChanges(); // DB 파일에 도장 쾅! (저장)
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"DB 저장 실패: {ex.Message}");
                    }
                });
            }

            // HUD 계산
            PitchOffset = state.Pitch * 3.55;
            HeadingOffset = -state.Yaw;

            // 배터리 경고
            if (state.BatteryPercent > 0 && state.BatteryPercent <= 20 && !ChatLog.Contains("배터리 부족"))
            {
                ChatLog += $"\n[경고] 기체 배터리 부족: {state.BatteryPercent}%";
            }

            // 착륙 및 비행 상태 초기화
            if (state.Altitude > 1.0) _isFlyingHigh = true; // 1m 이상 올라가면 비행 중으로 기록

            // 절대 규칙: 시동이 꺼지면(Disarmed) 무조건 모든 비행 기억을 지우고 대기 모드로!
            if (!state.IsArmed)
            {
                _isFlyingHigh = false; // 전생의 기억(비행 기록) 완벽 삭제!

                if (FlightMode != MODE_IDLE)
                {
                    ChatLog += "\n[시스템] 시동 꺼짐 확인. 대기(IDLE) 상태로 전환합니다.";
                }
            }
            // 시동이 켜져있고 TAKEOFF 모드인데 비정상적으로 땅에 곤두박질친 경우의 예외 처리
            else if (_isFlyingHigh && FlightMode == MODE_TAKEOFF && state.Altitude <= 0.5)
            {
                _isFlyingHigh = false;
                ChatLog += "\n[시스템] 고도 저하 감지. 대기 상태로 전환합니다.";
            }
        }

        // 수동 조종 엔진 켜기/끄기
        public async void ToggleManualControl(bool turnOn)
        {
            try
            {
                await _mavlinkService.SetManualControlModeAsync(turnOn);
            }
            catch (Exception ex)
            {
                ChatLog += $"\n[오류] 수동 모드 전환 실패: {ex.Message}";
            }
        }

        // 키보드 눌림/뗌에 따라 조이스틱 값 전달
        public void UpdateJoystick(short pitch, short roll, short thrust, short yaw)
        {
            _mavlinkService.SetJoystick(pitch, roll, thrust, yaw);
        }
        #endregion
    }
}
