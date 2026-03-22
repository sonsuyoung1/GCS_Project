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
        //현재 버튼 상태 캡처 메서드 추가 03/22
        private CommandStateSnapshot CaptureCommandState()
        {
            return new CommandStateSnapshot(
                FlightMode,
                Drone.IsConnected,
                Drone.IsArmed,
                _currentWaypointCount,
                SelectedTarget != null
            );
        }
        //필요할 때만 버튼 갱신하는 메서드 추가
        private void RefreshCommandStatesIfNeeded()
        {
            var current = CaptureCommandState();

            if (_lastCommandStateSnapshot == null || !_lastCommandStateSnapshot.Equals(current))
            {
                _lastCommandStateSnapshot = current;
                NotifyCommandStates();
            }
        }

        //ChatLog += 직접 붙이는 거 정리 -> 딜레이 막기 위함 03/22
        private void AppendLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            ChatLog += $"\n{message}";

            // 로그가 너무 길어지면 뒤쪽만 남김
            const int maxLen = 5000;
            const int keepLen = 3000;

            if (ChatLog.Length > maxLen)
            {
                ChatLog = ChatLog.Substring(ChatLog.Length - keepLen);
            }
        }
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
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    _currentWaypointCount = count;
                    RefreshCommandStatesIfNeeded();
                    AppendLog($"[시스템] 웨이포인트 개수 동기화 됨: {count}개");
                });
            };

            _mavlinkService.OnMissionCompleted += () =>
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    FlightMode = MODE_TAKEOFF;
                    AppendLog("모든 목적지에 도착하여 미션을 종료합니다.");
                });
            };

            _mavlinkService.OnLogReceived += (message) =>
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    AppendLog(message);
                });
            };

            _yoloMetaUdpService.OnLogReceived += (message) =>
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    AppendLog(message);
                });
            };

            _yoloMetaUdpService.OnTargetsReceived += (targets) =>
            {
                lock (_targetLock)
                {
                    _latestTargets = targets;
                }
            };

            try
            {
                await _mavlinkService.StartAsync();
                AppendLog("[시스템] MAVLink 연결 성공");

                await _yoloMetaUdpService.StartAsync();
                AppendLog("[시스템] YOLO UDP 메타 수신 시작");
            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 서비스 시작 실패: {ex.Message}");
            }
            finally
            {
                _mavStarting = false;
            }
            //yolo 최적화 03/22
            _targetUiTimer = new System.Windows.Threading.DispatcherTimer();
            _targetUiTimer.Interval = TimeSpan.FromMilliseconds(100);
            _targetUiTimer.Tick += (s, e) =>
            {
                List<DetectedTarget> snapshot;
                lock (_targetLock)
                {
                    snapshot = _latestTargets.ToList();
                }

                ApplyDetectedTargets(snapshot);
            };
            _targetUiTimer.Start();

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
                AppendLog($"[경고] 기체 배터리 부족: {state.BatteryPercent}%");
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
                    AppendLog("[시스템] 시동 꺼짐 확인. 대기(IDLE) 상태로 전환합니다.");
                }
            }
            // 시동이 켜져있고 TAKEOFF 모드인데 비정상적으로 땅에 곤두박질친 경우의 예외 처리
            else if (_isFlyingHigh && FlightMode == MODE_TAKEOFF && state.Altitude <= 0.5)
            {
                FlightMode = MODE_IDLE;
                _isFlyingHigh = false;
                AppendLog("[시스템] 고도 저하 감지. 대기 상태로 전환합니다.");
            }

            RefreshCommandStatesIfNeeded(); //03/22
        }

        private void ApplyDetectedTargets(List<DetectedTarget> targets)
        {
            var incomingById = targets.ToDictionary(t => t.ID);

            // 1) 기존 타겟 업데이트 + 새 타겟 추가
            foreach (var incoming in targets)
            {
                var existing = _detectedTargets.FirstOrDefault(t => t.ID == incoming.ID);

                if (existing != null)
                {
                    existing.TargetType = incoming.TargetType;
                    existing.Lat = incoming.Lat;
                    existing.Lng = incoming.Lng;
                    existing.Bx = incoming.Bx;
                    existing.By = incoming.By;
                    existing.Bw = incoming.Bw;
                    existing.Bh = incoming.Bh;
                }
                else
                {
                    _detectedTargets.Add(incoming);
                }
            }

            // 2) 이번 프레임에 없는 타겟 제거
            for (int i = _detectedTargets.Count - 1; i >= 0; i--)
            {
                var existing = _detectedTargets[i];

                if (!incomingById.ContainsKey(existing.ID))
                {
                    if (SelectedTarget?.ID == existing.ID)
                    {
                        SelectedTarget = null;
                    }

                    _detectedTargets.RemoveAt(i);
                }
            }

            // 3) 선택된 타겟 다시 연결
            if (SelectedTarget != null)
            {
                var matched = _detectedTargets.FirstOrDefault(t => t.ID == SelectedTarget.ID);

                if (matched != null)
                {
                    if (!ReferenceEquals(SelectedTarget, matched))
                    {
                        SelectedTarget = matched;
                    }

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
                AppendLog("[시스템] 수동 조종 모드 진입");
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
                AppendLog("[시스템] 수동 조종 모드 종료 - 제자리 유지");
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
