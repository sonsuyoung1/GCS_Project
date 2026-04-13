using Asv.Mavlink.Minimal;
using GCS_Project.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Windows;
using GCS_Project.Data;

namespace GCS_Project.ViewModels
{
    public partial class ViewModel
    {
        private DateTime _lastDbSaveTime = DateTime.MinValue;

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
        private async void InitializeService()
        {
            if (_mavStarting) return;
            _mavStarting = true;

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
                    switch (newMode)
                    {
                        case "TAKEOFF":
                            FlightMode = MODE_TAKEOFF;
                            break;

                        case "HOLD":
                        case "POSCTL":
                        case "ALTCTL":
                            FlightMode = MODE_HOLD;
                            break;

                        case "MISSION":
                            FlightMode = MODE_MISSION;
                            break;

                        case "MANUAL":
                            FlightMode = MODE_MANUAL;
                            break;

                        case "RTL":
                            FlightMode = MODE_HOLD;
                            break;

                        case "LAND":
                            FlightMode = MODE_TAKEOFF;
                            break;

                        default:
                            break;
                    }

                    RefreshCommandStatesIfNeeded();
                });
            };

            // 지연 해결할때까지 주석처리
            /*_mavlinkService.OnLogReceived += (message) =>
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
            };*/

            // 욜로 딜레이 확인하려고 잠시 주석 04/12
            /*            _yoloMetaUdpService.OnTargetsReceived += (targets) =>
                        {
                            Application.Current.Dispatcher.BeginInvoke(() =>
                            {
                                ApplyDetectedTargets(targets);
                            });
                        };*/
            //위에 주석 풀고 아래 삭제해야돼 04/12
            _yoloMetaUdpService.OnTargetsReceived += (targets) =>
            {
                lock (_pendingTargetsLock)
                {
                    _pendingTargetsQueue.Enqueue(new PendingTargets
                    {
                        ApplyAtUtc = DateTime.UtcNow.AddMilliseconds(_metaDisplayDelayMs),
                        Targets = targets
                    });

                    // 큐 폭발 방지
                    while (_pendingTargetsQueue.Count > 30)
                        _pendingTargetsQueue.Dequeue();
                }
            };

            try
            {
                await _mavlinkService.StartAsync("0.0.0.0", 14600, "192.168.0.25", 14551);
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

        private void ApplyAllState(DroneState state)
        {
            // HUD
            _uiDrone.Altitude = state.Altitude;
            _uiDrone.Speed = state.Speed;
            _uiDrone.Roll = state.Roll;
            _uiDrone.Pitch = state.Pitch;
            _uiDrone.Yaw = state.Yaw;

            double newPitchOffset = state.Pitch * 3.55;
            double newHeadingOffset = -state.Yaw;

            if (PitchOffset != newPitchOffset) PitchOffset = newPitchOffset;
            if (HeadingOffset != newHeadingOffset) HeadingOffset = newHeadingOffset;

            // STATUS
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

            // 배터리 경고 상태만 내부 플래그 관리
            bool batteryWarning = state.BatteryPercent > 0 && state.BatteryPercent <= 20;
            if (batteryWarning && !_batteryWarningShown)
            {
                _batteryWarningShown = true;
            }
            else if (!batteryWarning)
            {
                _batteryWarningShown = false;
            }

            // 비행 상태 추적
            if (state.Altitude > 1.0)
            {
                _isFlyingHigh = true;
            }

            if (!state.IsArmed)
            {
                _isFlyingHigh = false;

                if (FlightMode != MODE_IDLE)
                {
                    FlightMode = MODE_IDLE;
                }
            }
            else if (_isFlyingHigh && FlightMode == MODE_TAKEOFF && state.Altitude <= 0.5)
            {
                FlightMode = MODE_IDLE;
                _isFlyingHigh = false;
            }

            // MAP
            if (!(state.Latitude == 0 && state.Longitude == 0))
            {
                PositionUpdated?.Invoke(state.Latitude, state.Longitude, state.Yaw);
            }

            if (state.TakeoffLat.HasValue && state.TakeoffLng.HasValue && !_isTakeoffMarkerDrawn)
            {
                TakeoffPointSet?.Invoke(state.TakeoffLat.Value, state.TakeoffLng.Value);
                _isTakeoffMarkerDrawn = true;
            }

            if ((DateTime.UtcNow - _lastDbSaveTime).TotalMilliseconds > 1000)
            {
                _lastDbSaveTime = DateTime.UtcNow;

                var lat = state.Latitude;
                var lng = state.Longitude;
                var alt = state.Altitude;
                var speed = state.Speed;
                var battery = state.BatteryPercent;
                var flightMode = this.FlightMode;

                if (!(lat == 0 && lng == 0))
                {
                    Task.Run(() =>
                    {
                        try
                        {
                            using (var db = new GCS_Project.Data.AppDbContext())
                            {
                                var newLog = new FlightLog
                                {
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
            }

            RefreshCommandStatesIfNeeded();
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
                    existing.IsBeingTracked = incoming.IsBeingTracked;
                }
                else
                {
                    incoming.DisplayBx = incoming.Bx;
                    incoming.DisplayBy = incoming.By;
                    incoming.DisplayBw = incoming.Bw;
                    incoming.DisplayBh = incoming.Bh;

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

        //나중에 삭제해야됨, 욜로 딜레이용 04/12
        public void FlushDelayedTargets()
        {
            List<DetectedTarget>? ready = null;

            lock (_pendingTargetsLock)
            {
                if (_pendingTargetsQueue.Count == 0)
                    return;

                var now = DateTime.UtcNow;

                while (_pendingTargetsQueue.Count > 0 &&
                       _pendingTargetsQueue.Peek().ApplyAtUtc <= now)
                {
                    ready = _pendingTargetsQueue.Dequeue().Targets;
                }
            }

            if (ready != null)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    ApplyDetectedTargets(ready);
                });
            }
        }


        // 수동 조종 엔진 켜기/끄기
        public async Task ToggleManualControl(bool turnOn)
        {
            try
            {
                await _mavlinkService.SetFlightMode(
                    MavModeFlag.MavModeFlagCustomModeEnabled,
                    2
                );

                _mavlinkService.StartManualControl();

                // 시작하자마자 중립 입력 넣기
                _mavlinkService.SetJoystick(0, 0, 500, 0);

                FlightMode = MODE_MANUAL;
                AppendLog("[시스템] 수동 조종 모드 진입");
            }
            catch (Exception ex)
            {
                // 1. 먼저 입력을 중립으로 돌림
                _mavlinkService.SetJoystick(0, 0, 500, 0);

                // 2. 중립 입력이 실제로 몇 번 나가게 잠깐 대기
                await Task.Delay(200);

                // 3. 그 다음 송신 중지
                _mavlinkService.StopManualControl();

                // 4. 상태 표시
                FlightMode = MODE_HOLD;
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
