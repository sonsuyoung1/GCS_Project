using Asv.IO;
using Asv.Mavlink;
using Asv.Mavlink.Common;
using Asv.Mavlink.Minimal;
using R3;
using System.Threading;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GCS_Project.xServices
{
    public partial class MavlinkService
    {
        #region [텔레메트리 핸들링 (Telemetry Hooks)]
        private void HookTelemetry(IClientDevice drone)
        {
            if (_telemetryHooked) return;
            _telemetryHooked = true;

            // 어떤 패킷이든 들어오면 살아있다는 방어 로직
            /*_router!.OnRxMessage
                .Subscribe(_ =>
                {
                    _lastHeartbeatUtc = DateTime.UtcNow;

                    if (!CurrentState.IsConnected)
                    {
                        CurrentState.IsConnected = true;
                        OnLogReceived?.Invoke("[MAV] 통신 연결됨");
                    }
                })
                .AddTo(_subs);*/
            //디버깅용
            _router!.OnRxMessage
                .Subscribe(_ =>
                {
                    _lastHeartbeatUtc = DateTime.UtcNow;

                    bool shouldLog = false;

                    lock (_stateLock)
                    {
                        if (!CurrentState.IsConnected)
                        {
                            CurrentState.IsConnected = true;
                            shouldLog = true;
                        }
                    }

                    if (shouldLog)
                    {
                        OnLogReceived?.Invoke("[MAV] 통신 연결됨");
                    }
                })
                .AddTo(_subs);

            

            // 1) 하트비트 감시
            var hb = drone.GetMicroservice<IHeartbeatClient>();
            if (hb != null)
            {
                hb.RawHeartbeat
                    .Subscribe(packet =>
                    {
                        _lastHeartbeatUtc = DateTime.UtcNow;

                        bool isArmed = (packet?.BaseMode & MavModeFlag.MavModeFlagSafetyArmed) != 0;

                        lock (_telemetryRawLock)
                        {
                            _rawIsConnected = true;
                            _rawIsArmed = isArmed;
                        }

                        lock (_stateLock)
                        {
                            if (CurrentState.IsArmed != isArmed)
                            {
                                CurrentState.IsArmed = isArmed;
                            }
                        }
                    })
                    .AddTo(_subs);
                //디버깅용
                hb.RawHeartbeat
                    .Subscribe(packet =>
                    {
                        _lastHeartbeatUtc = DateTime.UtcNow;

                        bool isArmed = (packet?.BaseMode & MavModeFlag.MavModeFlagSafetyArmed) != 0;

                        lock (_stateLock)
                        {
                            if (CurrentState.IsArmed != isArmed)
                            {
                                CurrentState.IsArmed = isArmed;
                            }
                        }
                    })
                    .AddTo(_subs);
            }
            else
            {
                OnLogReceived?.Invoke("[경고] 하트비트 서비스를 찾을 수 없습니다.");
            }

            // 2) GCS Heartbeat 송신 타이머
            Observable.Timer(TimeSpan.Zero, TimeSpan.FromSeconds(1))
                .Subscribe(async _ =>
                {
                    try
                    {
                        if (_router == null) return;

                        var gcsHeartbeat = new HeartbeatPacket
                        {
                            SystemId = (byte)GcsSystemId,
                            ComponentId = (byte)GcsComponentId,
                        };

                        gcsHeartbeat.Payload.Type = MavType.MavTypeGcs;
                        gcsHeartbeat.Payload.Autopilot = MavAutopilot.MavAutopilotInvalid;
                        gcsHeartbeat.Payload.SystemStatus = MavState.MavStateActive;

                        await _router.Send(gcsHeartbeat, _cts?.Token ?? CancellationToken.None);
                    }
                    catch (TaskCanceledException)
                    {
                        // 정상 종료
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Heartbeat 전송 에러] {ex.Message}");
                    }
                })
                .AddTo(_subs);

            // 3) 위치 정보
            /*            drone.GetMicroservice<IPositionClient>()?.GlobalPosition
                            .Subscribe(gp =>
                            {
                                if (gp == null) return;

                                CurrentState.Latitude = gp.Lat / 1e7;
                                CurrentState.Longitude = gp.Lon / 1e7;
                                CurrentState.Altitude = gp.Alt / 1000.0;
                            })
                            .AddTo(_subs);*/
            //디버깅용
            /*_router!.OnRxMessage
                .OfType<IProtocolMessage, GlobalPositionIntPacket>()
                .Subscribe(packet =>
                {
                    var now = DateTime.UtcNow;
                    _lastRawGpUtc = now;

                    lock (_stateLock)
                    {
                        CurrentState.DebugRxUtc = now;
                        CurrentState.Latitude = packet.Payload.Lat / 1e7;
                        CurrentState.Longitude = packet.Payload.Lon / 1e7;
                        //CurrentState.Altitude = packet.Payload.RelativeAlt / 1000.0;
                    }
                })
                .AddTo(_subs);*/

            _router!.OnRxMessage
                .OfType<IProtocolMessage, GlobalPositionIntPacket>()
                .Subscribe(packet =>
                {
                    var now = DateTime.UtcNow;
                    var seq = Interlocked.Increment(ref _rxSeq);

                    var lat = packet.Payload.Lat / 1e7;
                    var lon = packet.Payload.Lon / 1e7;
                    var relAlt = packet.Payload.RelativeAlt / 1000.0;

                    _lastRawGpUtc = now;

                    lock (_telemetryRawLock)
                    {
                        _rawLat = lat;
                        _rawLon = lon;
                        _rawRelAlt = relAlt;
                        _rawGpUtc = now;
                        _rawGpSeq = seq;
                    }

                    Interlocked.Exchange(ref _latestSeq, seq);

                    // 기존 CurrentState도 최소한 연결/다른 로직 때문에 남겨두고 싶으면 유지
                    lock (_stateLock)
                    {
                        CurrentState.DebugRxUtc = now;
                        CurrentState.Latitude = lat;
                        CurrentState.Longitude = lon;
                        CurrentState.Altitude = relAlt;
                    }

                    System.Diagnostics.Debug.WriteLine(
                        $"[GP-RX] seq={seq} relAlt={relAlt:F2} lat={lat:F6} lon={lon:F6} t={DateTime.Now:HH:mm:ss.fff}");
                })
                .AddTo(_subs);

            // 4) 속도 정보 (디버깅용 락)
            drone.GetMicroservice<IPositionClient>()?.VfrHud
                .Subscribe(vfr =>
                {
                    if (vfr == null) return;

                    var speed = Math.Round(vfr.Groundspeed, 2);
                    var now = DateTime.UtcNow;

                    lock (_telemetryRawLock)
                    {
                        _rawSpeed = speed;
                        _rawVfrUtc = now;
                    }

                    lock (_stateLock)
                    {
                        CurrentState.Speed = speed;
                    }
                })
                .AddTo(_subs);

            // 5) 배터리 정보
            /*            drone.GetMicroservice<ITelemetryClient>()?.Battery
                            .Subscribe(b =>
                            {
                                if (b == null) return;

                                CurrentState.BatteryPercent = b.BatteryRemaining;
                            })
                            .AddTo(_subs);*/
            // 디버깅용
            drone.GetMicroservice<ITelemetryClient>()?.Battery
                .Subscribe(b =>
                {
                    if (b == null) return;

                    lock (_telemetryRawLock)
                    {
                        _rawBattery = b.BatteryRemaining;
                    }

                    lock (_stateLock)
                    {
                        CurrentState.BatteryPercent = b.BatteryRemaining;
                    }
                })
                .AddTo(_subs);

            // 6) 자세 정보
            /*            _router!.OnRxMessage
                            .OfType<IProtocolMessage, AttitudePacket>()
                            .Subscribe(packet =>
                            {
                                CurrentState.DebugRxUtc = DateTime.UtcNow; // 디버깅용
                                CurrentState.Roll = packet.Payload.Roll * (180.0 / Math.PI);
                                CurrentState.Pitch = packet.Payload.Pitch * (180.0 / Math.PI);
                                CurrentState.Yaw = (packet.Payload.Yaw * (180.0 / Math.PI) + 360) % 360;
                            })
                            .AddTo(_subs);*/

            //디버깅용
            _router!.OnRxMessage
                .OfType<IProtocolMessage, AttitudePacket>()
                .Subscribe(packet =>
                {
                    var now = DateTime.UtcNow;
                    _lastRawAttUtc = now;

                    var roll = packet.Payload.Roll * (180.0 / Math.PI);
                    var pitch = packet.Payload.Pitch * (180.0 / Math.PI);
                    var yaw = (packet.Payload.Yaw * (180.0 / Math.PI) + 360) % 360;

                    lock (_telemetryRawLock)
                    {
                        _rawRoll = roll;
                        _rawPitch = pitch;
                        _rawYaw = yaw;
                        _rawAttUtc = now;
                    }

                    lock (_stateLock)
                    {
                        CurrentState.DebugRxUtc = now;
                        CurrentState.Roll = roll;
                        CurrentState.Pitch = pitch;
                        CurrentState.Yaw = yaw;
                    }
                })
                .AddTo(_subs);

            _router!.OnRxMessage
                .OfType<IProtocolMessage, LocalPositionNedPacket>()
                .Subscribe(packet =>
                {
                    var now = DateTime.UtcNow;
                    var localAlt = -packet.Payload.Z;

                    lock (_telemetryRawLock)
                    {
                        _rawLocalAlt = localAlt;
                        _rawLpnUtc = now;
                    }

                    lock (_stateLock)
                    {
                        CurrentState.LocalAltitude = localAlt;
                    }

                    System.Diagnostics.Debug.WriteLine(
                        $"[LPN-RX] localAlt={localAlt:F2} t={DateTime.Now:HH:mm:ss.fff}");
                })
                .AddTo(_subs);

            // 7) UI 갱신 타이머
            /*_uiUpdateTimer = Observable
                .Interval(TimeSpan.FromMilliseconds(100))
                .Subscribe(_ =>
                {
                    PublishState();
                })
                .AddTo(_subs);*/
            //디버깅용
            _uiUpdateTimer = Observable
                 .Interval(TimeSpan.FromMilliseconds(100))
                 .Subscribe(_ =>
                 {
                     PublishState();
                 })
                 .AddTo(_subs);

            /*_router!.OnRxMessage
                .OfType<IProtocolMessage, LocalPositionNedPacket>()
                .Subscribe(packet =>
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[LPN] x={packet.Payload.X:F2}, y={packet.Payload.Y:F2}, z={packet.Payload.Z:F2}, vz={packet.Payload.Vz:F2}");
                })
                .AddTo(_subs);*/

            //디버깅용 로그 타이머
            Observable.Interval(TimeSpan.FromSeconds(1))
                .Subscribe(_ =>
                {
                    var now = DateTime.UtcNow;
                    var gpAge = (now - _lastRawGpUtc).TotalMilliseconds;
                    var attAge = (now - _lastRawAttUtc).TotalMilliseconds;

                    OnLogReceived?.Invoke($"[RAW-AGE] GP={gpAge:F0}ms, ATT={attAge:F0}ms");
                })
                .AddTo(_subs);
        }

        private void HookCommandAck(IClientDevice drone)
        {
            var cmd = drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            cmd.OnCommandAck.Subscribe(ack =>
            {
                string commandName = ack.Command switch
                {
                    MavCmd.MavCmdNavTakeoff => "이륙",
                    MavCmd.MavCmdNavReturnToLaunch => "복귀",
                    MavCmd.MavCmdNavLand => "착륙",
                    MavCmd.MavCmdNavWaypoint => "목적지 이동",
                    _ => $"명령 {ack.Command}"
                };

                string resultText = ack.Result switch
                {
                    MavResult.MavResultAccepted => "수락됨",
                    MavResult.MavResultTemporarilyRejected => "일시적 거절",
                    MavResult.MavResultDenied => "거절됨",
                    MavResult.MavResultUnsupported => "지원 안 됨",
                    MavResult.MavResultFailed => "실행 실패",
                    _ => $"알 수 없음 ({ack.Result})"
                };

                // 성공 로그는 필요하면 살리고, 아니면 실패만 남기기
                if (ack.Result != MavResult.MavResultAccepted)
                {
                    OnLogReceived?.Invoke($"[명령 경고] {commandName} -> {resultText}");
                }
            }).AddTo(_subs);
        }

        private void StartLinkWatchdog()
        {
            if (_linkWatchdogTask != null && !_linkWatchdogTask.IsCompleted) return;

            _lastHeartbeatUtc = DateTime.UtcNow;

            _linkWatchdogTask = Task.Run(async () =>
            {
                try
                {
                    while (_cts != null && !_cts.Token.IsCancellationRequested)
                    {
                        var ageMs = (DateTime.UtcNow - _lastHeartbeatUtc).TotalMilliseconds;

                        /*if (ageMs > 3000 && CurrentState.IsConnected)
                        {
                            CurrentState.IsConnected = false;
                            OnLogReceived?.Invoke("[비상] 기체와의 통신이 끊겼습니다. (Heartbeat Timeout)");
                        }*/
                        // 디버깅용
                        bool shouldNotify = false;

                        lock (_stateLock)
                        {
                            if (ageMs > 3000 && CurrentState.IsConnected)
                            {
                                CurrentState.IsConnected = false;
                                shouldNotify = true;
                            }
                        }
                        
                        if (shouldNotify)
                        {
                            OnLogReceived?.Invoke("[비상] 기체와의 통신이 끊겼습니다. (Heartbeat Timeout)");
                        }
                        //여기 위에까지
                        await Task.Delay(200, _cts.Token);
                    }
                }
                catch (TaskCanceledException)
                {
                    // 정상 종료
                }
                catch (Exception ex)
                {
                    OnLogReceived?.Invoke($"[오류] 감시 프로세스 에러: {ex.Message}");
                }
                finally
                {
                    _linkWatchdogTask = null;
                }
            });
        }
        #endregion
    }
}