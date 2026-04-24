using Asv.Mavlink.Minimal;
using GCS_Project.Data;
using GCS_Project.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace GCS_Project.ViewModels
{
    public partial class ViewModel
    {
        private DateTime _lastDbSaveTime = DateTime.MinValue;

        #region [1. 버튼 상태 갱신]

        // 버튼 활성화에 영향을 주는 현재 상태를 스냅샷으로 만든다.
        private CommandStateSnapshot CaptureCommandState()
        {
            return new CommandStateSnapshot(
                FlightMode,
                Drone.IsConnected,
                Drone.IsArmed,
                _currentWaypointCount,
                SelectedTarget != null,
                IsStriking
            );
        }

        // 버튼 상태에 영향을 주는 값이 바뀐 경우에만 커맨드 상태를 갱신한다.
        private void RefreshCommandStatesIfNeeded()
        {
            var current = CaptureCommandState();

            if (_lastCommandStateSnapshot == null || !_lastCommandStateSnapshot.Equals(current))
            {
                _lastCommandStateSnapshot = current;
                NotifyCommandStates();
            }
        }

        #endregion


        #region [2. 로그 처리]

        // 로그를 버퍼에 누적하고 길이가 길어지면 일부만 유지한다.
        private void AppendLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            _chatLogBuffer.AppendLine(message);

            const int maxLen = 5000;
            const int keepLen = 3000;

            if (_chatLogBuffer.Length > maxLen)
            {
                var text = _chatLogBuffer.ToString();
                _chatLogBuffer.Clear();
                _chatLogBuffer.Append(text.Substring(text.Length - keepLen));
            }

            ChatLog = _chatLogBuffer.ToString();
        }

        #endregion


        #region [3. 서비스 초기화 및 이벤트 연결]

        // MAVLink와 YOLO UDP 서비스를 시작하고 이벤트를 ViewModel에 연결한다.
        private async void InitializeService()
        {
            if (_mavStarting) return;

            _mavStarting = true;

            SubscribeMavlinkEvents();
            SubscribeYoloEvents();

            try
            {
                await _mavlinkService.StartAsync("0.0.0.0", 14550, "192.168.0.25", 14551);
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
        }

        // MAVLink 서비스에서 발생하는 상태/모드/미션 이벤트를 연결한다.
        private void SubscribeMavlinkEvents()
        {
            _mavlinkService.OnDataUpdated += (state) =>
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    ApplyAllState(state);
                });
            };

            _mavlinkService.OnWaypointCountChanged += (count) =>
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    _currentWaypointCount = count;
                    RefreshCommandStatesIfNeeded();
                });
            };

            _mavlinkService.OnMissionCompleted += () =>
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    FlightMode = MODE_TAKEOFF;
                });
            };

            _mavlinkService.OnFlightModeChanged += (newMode) =>
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    ApplyFlightModeFromPx4(newMode);
                    RefreshCommandStatesIfNeeded();
                });
            };

            // 로그 출력이 UI 지연을 만들 경우 아래 이벤트 구독은 비활성화한다.
            /*
            _mavlinkService.OnLogReceived += (message) =>
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    AppendLog(message);
                });
            };
            */
        }

        // YOLO UDP 서비스에서 수신한 타겟 목록 이벤트를 연결한다.
        private void SubscribeYoloEvents()
        {
            _yoloMetaUdpService.OnTargetsReceived += (targets) =>
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    ApplyDetectedTargets(targets);
                });
            };

            // 로그 출력이 UI 지연을 만들 경우 아래 이벤트 구독은 비활성화한다.
            /*
            _yoloMetaUdpService.OnLogReceived += (message) =>
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    AppendLog(message);
                });
            };
            */
        }

        // PX4에서 받은 실제 비행 모드를 ViewModel의 FlightMode로 변환한다.
        private void ApplyFlightModeFromPx4(string newMode)
        {
            switch (newMode)
            {
                case "TAKEOFF":
                    FlightMode = MODE_TAKEOFF;
                    break;

                case "HOLD":
                case "POSCTL":
                case "ALTCTL":
                case "RTL":
                    FlightMode = MODE_HOLD;
                    break;

                case "MISSION":
                    FlightMode = MODE_MISSION;
                    break;

                case "MANUAL":
                    FlightMode = MODE_MANUAL;
                    break;

                case "LAND":
                    FlightMode = MODE_TAKEOFF;
                    break;

                default:
                    break;
            }
        }

        #endregion


        #region [4. 드론 상태 반영]

        // PX4에서 수신한 드론 상태를 UI용 Drone 객체와 화면 이벤트에 반영한다.
        private void ApplyAllState(DroneState state)
        {
            ApplyHudState(state);
            ApplyStatusState(state);
            UpdateBatteryWarningState(state);
            UpdateFlightState(state);
            UpdateMapState(state);
            SaveFlightLogIfNeeded(state);

            RefreshCommandStatesIfNeeded();
        }

        // HUD에 표시할 자세, 고도, 속도 정보를 갱신한다.
        private void ApplyHudState(DroneState state)
        {
            _uiDrone.Altitude = state.Altitude;
            _uiDrone.Speed = state.Speed;
            _uiDrone.Roll = state.Roll;
            _uiDrone.Pitch = state.Pitch;
            _uiDrone.Yaw = state.Yaw;

            double newPitchOffset = state.Pitch * 3.55;
            double newHeadingOffset = -state.Yaw;

            if (PitchOffset != newPitchOffset) PitchOffset = newPitchOffset;
            if (HeadingOffset != newHeadingOffset) HeadingOffset = newHeadingOffset;
        }

        // 연결, 시동, 배터리, 위치 같은 드론 상태 정보를 갱신한다.
        private void ApplyStatusState(DroneState state)
        {
            _uiDrone.Latitude = state.Latitude;
            _uiDrone.Longitude = state.Longitude;
            _uiDrone.IsConnected = state.IsConnected;
            _uiDrone.IsArmed = state.IsArmed;
            _uiDrone.BatteryPercent = state.BatteryPercent;
            _uiDrone.TakeoffLat = state.TakeoffLat;
            _uiDrone.TakeoffLng = state.TakeoffLng;
            _uiDrone.IsTargetDetected = state.IsTargetDetected;
            _uiDrone.TargetLat = state.TargetLat;
            _uiDrone.TargetLng = state.TargetLng;
            _uiDrone.TargetAltitude = state.TargetAltitude;
            _uiDrone.TargetSpeed = state.TargetSpeed;
        }

        // 배터리 경고 표시 여부를 내부 플래그로 관리한다.
        private void UpdateBatteryWarningState(DroneState state)
        {
            bool batteryWarning = state.BatteryPercent > 0 && state.BatteryPercent <= 20;

            if (batteryWarning && !_batteryWarningShown)
            {
                _batteryWarningShown = true;
            }
            else if (!batteryWarning)
            {
                _batteryWarningShown = false;
            }
        }

        // Armed/Disarmed 변화와 착륙 후 IDLE 복귀 상태를 처리한다.
        private void UpdateFlightState(DroneState state)
        {
            if (state.Altitude > 1.0)
            {
                _isFlyingHigh = true;
            }

            bool isCurrentlyArmed = state.IsArmed;

            if (_wasArmed != isCurrentlyArmed)
            {
                if (!isCurrentlyArmed)
                {
                    HandleDisarmedState();
                }

                _wasArmed = isCurrentlyArmed;
            }

            if (_isFlyingHigh && FlightMode == MODE_TAKEOFF && state.Altitude <= 0.5)
            {
                FlightMode = MODE_IDLE;
                _isFlyingHigh = false;
            }
        }

        // 기체가 Disarm 상태로 바뀌었을 때 타격 상태와 비행 모드를 정리한다.
        private void HandleDisarmedState()
        {
            _isFlyingHigh = false;

            if (IsStriking)
            {
                IsStriking = false;
                AppendLog("[시스템] 기체 시동이 꺼져 타격 모드를 자동 해제합니다.");
            }

            if (FlightMode != MODE_IDLE)
            {
                FlightMode = MODE_IDLE;
                AppendLog("[시스템] 시동 꺼짐 확인. 대기(IDLE) 상태로 전환합니다.");
            }
        }

        // 드론 위치와 이륙 지점을 지도에 반영한다.
        private void UpdateMapState(DroneState state)
        {
            if (!(state.Latitude == 0 && state.Longitude == 0))
            {
                PositionUpdated?.Invoke(state.Latitude, state.Longitude, state.Yaw);
            }

            if (state.TakeoffLat.HasValue && state.TakeoffLng.HasValue && !_isTakeoffMarkerDrawn)
            {
                TakeoffPointSet?.Invoke(state.TakeoffLat.Value, state.TakeoffLng.Value);
                _isTakeoffMarkerDrawn = true;
            }
        }

        // 1초 간격으로 드론 비행 상태를 DB에 저장한다.
        private void SaveFlightLogIfNeeded(DroneState state)
        {
            if ((DateTime.UtcNow - _lastDbSaveTime).TotalMilliseconds <= 1000)
                return;

            _lastDbSaveTime = DateTime.UtcNow;

            double lat = state.Latitude;
            double lng = state.Longitude;

            if (lat == 0 && lng == 0)
                return;

            double alt = state.Altitude;
            double speed = state.Speed;
            int battery = state.BatteryPercent;
            string flightMode = FlightMode;
            int flightId = _currentFlightId;

            Task.Run(() =>
            {
                try
                {
                    using (var db = new AppDbContext())
                    {
                        var newLog = new FlightLog
                        {
                            FlightId = flightId,
                            Timestamp = DateTime.Now,
                            Latitude = lat,
                            Longitude = lng,
                            Altitude = alt,
                            Speed = speed,
                            BatteryPercent = battery,
                            FlightMode = flightMode
                        };

                        db.FlightLogs.Add(newLog);
                        db.SaveChanges();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"DB 저장 실패: {ex.Message}");
                }
            });
        }

        #endregion


        #region [5. 탐지 타겟 반영]

        // YOLO에서 받은 타겟 목록을 기존 타겟 목록에 병합하고 선택 상태를 유지한다.
        private void ApplyDetectedTargets(List<DetectedTarget> targets)
        {
            var incomingById = targets.ToDictionary(t => t.ID);

            UpdateOrAddTargets(targets);
            RemoveMissingTargets(incomingById);
            ReconnectSelectedTarget();
        }

        // 기존 타겟은 갱신하고 새 타겟은 목록에 추가한다.
        private void UpdateOrAddTargets(List<DetectedTarget> targets)
        {
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
                    existing.IsBeingTracked = incoming.IsBeingTracked;
                }
                else
                {
                    _detectedTargets.Add(incoming);
                }
            }
        }

        // 이번 프레임에 없는 타겟을 목록에서 제거한다.
        private void RemoveMissingTargets(Dictionary<int, DetectedTarget> incomingById)
        {
            for (int i = _detectedTargets.Count - 1; i >= 0; i--)
            {
                var existing = _detectedTargets[i];

                if (incomingById.ContainsKey(existing.ID))
                    continue;

                if (SelectedTarget?.ID == existing.ID)
                {
                    SelectedTarget = null;

                    if (IsStriking)
                    {
                        AppendLog("[경고] 돌진 중 타겟을 시야에서 상실했습니다. 마지막 좌표로 계속 비행합니다.");
                    }
                }

                _detectedTargets.RemoveAt(i);
            }
        }

        // 선택된 타겟이 갱신된 목록 안에 있으면 새 객체와 다시 연결한다.
        private void ReconnectSelectedTarget()
        {
            if (SelectedTarget == null)
                return;

            var matched = _detectedTargets.FirstOrDefault(t => t.ID == SelectedTarget.ID);

            if (matched == null)
            {
                SelectedTarget = null;
                return;
            }

            if (!ReferenceEquals(SelectedTarget, matched))
            {
                SelectedTarget = matched;
            }

            matched.IsBeingTracked = true;
        }

        #endregion


        #region [6. 수동 조종]

        // 수동 조종 모드를 켜거나 끈다.
        public async Task ToggleManualControl(bool turnOn)
        {
            if (turnOn)
            {
                await EnterManualControl();
            }
            else
            {
                await ExitManualControl();
            }
        }

        // 수동 조종 모드로 진입하고 가상 조이스틱 송신을 시작한다.
        private async Task EnterManualControl()
        {
            try
            {
                await _mavlinkService.SetFlightMode(
                    MavModeFlag.MavModeFlagCustomModeEnabled,
                    2
                );

                _mavlinkService.StartManualControl();
                _mavlinkService.SetJoystick(0, 0, 500, 0);

                FlightMode = MODE_MANUAL;
                AppendLog("[시스템] 수동 조종 모드 진입");
            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 수동 조종 모드 진입 실패: {ex.Message}");
            }
        }

        // 수동 조종 모드를 종료하고 기체를 제자리 유지 상태로 전환한다.
        private async Task ExitManualControl()
        {
            try
            {
                _mavlinkService.SetJoystick(0, 0, 500, 0);
                await Task.Delay(200);

                _mavlinkService.StopManualControl();

                FlightMode = MODE_HOLD;
                AppendLog("[시스템] 수동 조종 모드 종료 - 제자리 유지");
            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 수동 조종 모드 종료 실패: {ex.Message}");
            }
        }

        // 키보드 입력값을 가상 조이스틱 값으로 MAVLink 서비스에 전달한다.
        public void UpdateJoystick(short pitch, short roll, short thrust, short yaw)
        {
            _mavlinkService.SetJoystick(pitch, roll, thrust, yaw);
        }

        #endregion
    }
}