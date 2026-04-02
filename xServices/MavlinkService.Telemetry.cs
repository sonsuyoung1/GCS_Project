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

            // 1) 하트비트 감시
            var hb = drone.GetMicroservice<IHeartbeatClient>();
            if (hb != null)
            {
                hb.RawHeartbeat
                    .Subscribe(packet =>
                    {
                        var now = DateTime.UtcNow;
                        bool isArmed = (packet?.BaseMode & MavModeFlag.MavModeFlagSafetyArmed) != 0;

                        _lastHeartbeatUtc = now;

                        lock (_telemetryRawLock)
                        {
                            _rawIsConnected = true;
                            _rawIsArmed = isArmed;
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
                        OnLogReceived?.Invoke($"[경고] GCS Heartbeat 전송 실패: {ex.Message}");
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

            _router!.OnRxMessage
                .OfType<IProtocolMessage, GlobalPositionIntPacket>()
                .Subscribe(packet =>
                {
                    var now = DateTime.UtcNow;

                    var lat = packet.Payload.Lat / 1e7;
                    var lon = packet.Payload.Lon / 1e7;
                    var relAlt = packet.Payload.RelativeAlt / 1000.0;

                    lock (_telemetryRawLock)
                    {
                        _rawLat = lat;
                        _rawLon = lon;
                        _rawRelAlt = relAlt;
                        _rawGpUtc = now;
                    }
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
            drone.GetMicroservice<ITelemetryClient>()?.Battery
                .Subscribe(b =>
                {
                    if (b == null) return;

                    lock (_telemetryRawLock)
                    {
                        _rawBattery = b.BatteryRemaining;
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
                })
                .AddTo(_subs);

            // 7) UI 갱신 타이머
            _uiUpdateTimer = Observable
                .Interval(TimeSpan.FromMilliseconds(50))
                .Subscribe(_ =>
                {
                    PublishState();
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

                        bool shouldNotify = false;

                        lock (_telemetryRawLock)
                        {
                            if (ageMs > 3000 && _rawIsConnected)
                            {
                                _rawIsConnected = false;
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