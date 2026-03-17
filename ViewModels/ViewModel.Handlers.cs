using Asv.Mavlink.Minimal;
using GCS_Project.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace GCS_Project.ViewModels
{
    public partial class ViewModel
    {
        #region [이벤트 핸들러 (Event Handlers)]
        private async void InitializeService()
        {
            if (_mavStarting) return;
            _mavStarting = true;

            // ===== 1. 이벤트 구독 먼저 =====
            _mavlinkService.OnDataUpdated += (state) =>
            {
                Application.Current.Dispatcher.BeginInvoke(() => HandleDroneData(state));
            };

            _mavlinkService.OnWaypointCountChanged += (count) =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _currentWaypointCount = count;
                    NotifyCommandStates();
                    ChatLog += $"\n[시스템] 웨이포인트 개수 동기화 됨: {count}개";
                });
            };

            _mavlinkService.OnMissionCompleted += () =>
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    FlightMode = MODE_TAKEOFF;
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

            _yoloMetaUdpService.OnLogReceived += (message) =>
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    ChatLog += $"\n{message}";
                });
            };

            _yoloMetaUdpService.OnTargetsReceived += (targets) =>
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    ApplyDetectedTargets(targets);
                });
            };

            try
            {
                await _mavlinkService.StartAsync();
                ChatLog += "\n[시스템] MAVLink 연결 성공";

                await _yoloMetaUdpService.StartAsync();
                ChatLog += "\n[시스템] YOLO UDP 메타 수신 시작";
            }
            catch (Exception ex)
            {
                ChatLog += $"\n[오류] 서비스 시작 실패: {ex.Message}";
            }
            finally
            {
                _mavStarting = false;
            }


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
                });
            };*/
        }

        private void HandleDroneData(DroneState state)
        {
            if (!Drone.IsConnected)
            {
                Drone.IsConnected = true;
            }

            if (state.Latitude == 0 && state.Longitude == 0) return;

            // 2. 기존 위치 업데이트 로직
            if ((DateTime.UtcNow - _lastMapUpdate).TotalMilliseconds > 200)
            {
                _lastMapUpdate = DateTime.UtcNow;

                // 1. 드론 현재 위치 업데이트
                PositionUpdated?.Invoke(state.Latitude, state.Longitude, state.Yaw);

                // 2. 이륙 마커 업데이트 (스위치를 확인해서 '딱 한 번만' 그립니다!)
                if (state.TakeoffLat.HasValue && state.TakeoffLng.HasValue && !_isTakeoffMarkerDrawn)
                {
                    TakeoffPointSet?.Invoke(state.TakeoffLat.Value, state.TakeoffLng.Value);
                    _isTakeoffMarkerDrawn = true; // "나 그렸어! 이제 다시 부르지 마!"
                }
            }

            // 3. HUD 계산
            PitchOffset = state.Pitch * 3.55;
            HeadingOffset = -state.Yaw;

            // 4. 배터리 경고
            if (state.BatteryPercent > 0 && state.BatteryPercent <= 20 && !ChatLog.Contains("배터리 부족"))
            {
                ChatLog += $"\n[경고] 기체 배터리 부족: {state.BatteryPercent}%";
            }

            // 6. 착륙 감지
            // 6. 착륙 및 비행 상태 초기화 (완벽 방어막)
            if (state.Altitude > 1.0)
            {
                _isFlyingHigh = true; // 1m 이상 올라가면 비행 중으로 기록
            }

            // 절대 규칙: 시동이 꺼지면(Disarmed) 무조건 모든 비행 기억을 지우고 대기 모드로!
            if (!state.IsArmed)
            {
                _isFlyingHigh = false; // 전생의 기억(비행 기록) 완벽 삭제!

                if (FlightMode != MODE_IDLE)
                {
                    FlightMode = MODE_IDLE; // UI도 IDLE로 안전하게 복귀
                    ChatLog += "\n[시스템] 시동 꺼짐 확인. 대기(IDLE) 상태로 전환합니다.";
                }
            }
            // 시동이 켜져있고 TAKEOFF 모드인데 비정상적으로 땅에 곤두박질친 경우의 예외 처리
            else if (_isFlyingHigh && FlightMode == MODE_TAKEOFF && state.Altitude <= 0.5)
            {
                FlightMode = MODE_IDLE;
                _isFlyingHigh = false;
                ChatLog += "\n[시스템] 고도 저하 감지. 대기 상태로 전환합니다.";
            }
        }

        private void ApplyDetectedTargets(List<DetectedTarget> targets)
        {
            _detectedTargets.Clear();

            foreach (var target in targets)
            {
                _detectedTargets.Add(target);
            }
            if (SelectedTarget != null)
            {
                DetectedTarget? matched = null;

                foreach (var t in _detectedTargets)
                {
                    if (t.ID == SelectedTarget.ID)
                    {
                        matched = t;
                        break;
                    }
                }

                if (matched != null)
                {
                    SelectedTarget = matched;
                    matched.IsBeingTracked = true;
                }
                else
                {
                    SelectedTarget = null;
                }
            }
        }

        // 수동 조종 엔진 켜기/끄기
        public async Task ToggleManualControl(bool turnOn)
        {
            if (turnOn)
            {
                await _mavlinkService.SetFlightMode(
                    MavModeFlag.MavModeFlagCustomModeEnabled,
                    2
                );

                _mavlinkService.StartManualControl();

                // 시작하자마자 중립 입력 넣기
                _mavlinkService.SetJoystick(0, 0, 500, 0);

                FlightMode = "MANUAL";
                ChatLog += "\n[시스템] 수동 조종 모드 진입";
            }
            else
            {
                // 1. 먼저 입력을 중립으로 돌림
                _mavlinkService.SetJoystick(0, 0, 500, 0);

                // 2. 중립 입력이 실제로 몇 번 나가게 잠깐 대기
                await Task.Delay(200);

                // 3. 그 다음 송신 중지
                _mavlinkService.StopManualControl();

                // 4. 상태 표시
                FlightMode = "HOLD";
                ChatLog += "\n[시스템] 수동 조종 모드 종료 - 제자리 유지";
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
