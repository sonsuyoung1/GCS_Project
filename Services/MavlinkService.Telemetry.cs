using Asv.IO;
using Asv.Mavlink;
using Asv.Mavlink.Common;
using Asv.Mavlink.Minimal;
using R3;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace GCS_Project.Services
{
    public partial class MavlinkService
    {
        #region [1. 텔레메트리 연결]

        // PX4 텔레메트리와 명령 응답 이벤트를 연결한다.
        private void HookTelemetry(IClientDevice drone)
        {
            if (_telemetryHooked) return;

            _telemetryHooked = true;

            SubscribeHeartbeat(drone);
            StartGcsHeartbeatSender();
            SubscribeGlobalPosition();
            SubscribeVfrHud(drone);
            SubscribeBattery(drone);
            SubscribeAttitude();
            SubscribeLocalPosition();
            StartUiUpdateTimer();
        }

        #endregion


        #region [2. Heartbeat 처리]

        // PX4 Heartbeat에서 연결 상태, Arm 상태, 실제 비행 모드를 읽는다.
        private void SubscribeHeartbeat(IClientDevice drone)
        {
            var heartbeat = drone.GetMicroservice<IHeartbeatClient>();

            if (heartbeat == null)
            {
                OnLogReceived?.Invoke("[경고] 하트비트 서비스를 찾을 수 없습니다.");
                return;
            }

            heartbeat.RawHeartbeat
                .Subscribe(packet =>
                {
                    DateTime now = DateTime.UtcNow;

                    bool isArmed =
                        (packet?.BaseMode & MavModeFlag.MavModeFlagSafetyArmed) != 0;

                    _lastHeartbeatUtc = now;

                    lock (_telemetryRawLock)
                    {
                        _rawIsConnected = true;
                        _rawIsArmed = isArmed;
                    }

                    UpdateFlightModeFromHeartbeat(packet?.CustomMode ?? 0);
                })
                .AddTo(_subs);
        }

        // Heartbeat의 CustomMode를 해석해서 비행 모드가 바뀌었을 때 ViewModel에 알린다.
        private void UpdateFlightModeFromHeartbeat(uint customMode)
        {
            string currentMode = ParsePX4Mode(customMode);

            if (_lastParsedMode == currentMode)
                return;

            _lastParsedMode = currentMode;
            OnFlightModeChanged?.Invoke(currentMode);
        }

        // GCS가 살아 있음을 PX4에 주기적으로 알리는 Heartbeat를 전송한다.
        private void StartGcsHeartbeatSender()
        {
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

                        await _router.Send(
                            gcsHeartbeat,
                            _cts?.Token ?? CancellationToken.None
                        );
                    }
                    catch (TaskCanceledException)
                    {
                        // 서비스 종료 중 발생하는 정상 취소
                    }
                    catch (Exception ex)
                    {
                        OnLogReceived?.Invoke($"[경고] GCS Heartbeat 전송 실패: {ex.Message}");
                    }
                })
                .AddTo(_subs);
        }

        #endregion


        #region [3. 위치/속도/고도 수신]

        // PX4의 전역 GPS 위치와 상대 고도를 수신한다.
        private void SubscribeGlobalPosition()
        {
            if (_router == null) return;

            _router.OnRxMessage
                .OfType<IProtocolMessage, GlobalPositionIntPacket>()
                .Subscribe(packet =>
                {
                    DateTime now = DateTime.UtcNow;

                    double lat = packet.Payload.Lat / 1e7;
                    double lon = packet.Payload.Lon / 1e7;
                    double relAlt = packet.Payload.RelativeAlt / 1000.0;

                    lock (_telemetryRawLock)
                    {
                        _rawLat = lat;
                        _rawLon = lon;
                        _rawRelAlt = relAlt;
                        _rawGpUtc = now;
                    }
                })
                .AddTo(_subs);
        }

        // PX4의 지상 속도를 수신한다.
        private void SubscribeVfrHud(IClientDevice drone)
        {
            drone.GetMicroservice<IPositionClient>()?.VfrHud
                .Subscribe(vfr =>
                {
                    if (vfr == null) return;

                    DateTime now = DateTime.UtcNow;
                    double speed = Math.Round(vfr.Groundspeed, 2);

                    lock (_telemetryRawLock)
                    {
                        _rawSpeed = speed;
                        _rawVfrUtc = now;
                    }
                })
                .AddTo(_subs);
        }

        // PX4의 로컬 NED 좌표에서 고도 값을 수신한다.
        private void SubscribeLocalPosition()
        {
            if (_router == null) return;

            _router.OnRxMessage
                .OfType<IProtocolMessage, LocalPositionNedPacket>()
                .Subscribe(packet =>
                {
                    DateTime now = DateTime.UtcNow;
                    double localAlt = -packet.Payload.Z;

                    lock (_telemetryRawLock)
                    {
                        _rawLocalAlt = localAlt;
                        _rawLpnUtc = now;
                    }
                })
                .AddTo(_subs);
        }

        #endregion


        #region [4. 배터리/자세 수신]

        // PX4의 배터리 잔량을 수신한다.
        private void SubscribeBattery(IClientDevice drone)
        {
            drone.GetMicroservice<ITelemetryClient>()?.Battery
                .Subscribe(battery =>
                {
                    if (battery == null) return;

                    lock (_telemetryRawLock)
                    {
                        _rawBattery = battery.BatteryRemaining;
                    }
                })
                .AddTo(_subs);
        }

        // PX4의 Roll, Pitch, Yaw 자세 값을 수신한다.
        private void SubscribeAttitude()
        {
            if (_router == null) return;

            _router.OnRxMessage
                .OfType<IProtocolMessage, AttitudePacket>()
                .Subscribe(packet =>
                {
                    DateTime now = DateTime.UtcNow;

                    double roll = packet.Payload.Roll * (180.0 / Math.PI);
                    double pitch = packet.Payload.Pitch * (180.0 / Math.PI);
                    double yaw = (packet.Payload.Yaw * (180.0 / Math.PI) + 360) % 360;

                    lock (_telemetryRawLock)
                    {
                        _rawRoll = roll;
                        _rawPitch = pitch;
                        _rawYaw = yaw;
                        _rawAttUtc = now;
                    }
                })
                .AddTo(_subs);
        }

        #endregion


        #region [5. UI 상태 발행]

        // 일정 주기로 raw 텔레메트리를 CurrentState로 묶어 ViewModel에 전달한다.
        private void StartUiUpdateTimer()
        {
            _uiUpdateTimer = Observable
                .Interval(TimeSpan.FromMilliseconds(50))
                .Subscribe(_ =>
                {
                    PublishState();
                })
                .AddTo(_subs);
        }

        #endregion


        #region [6. PX4 모드 해석]

        // PX4 CustomMode 값을 GCS에서 사용하는 문자열 비행 모드로 변환한다.
        private string ParsePX4Mode(uint customMode)
        {
            uint mainMode = (customMode >> 16) & 0xFF;
            uint subMode = (customMode >> 24) & 0xFF;

            if (mainMode == 4)
            {
                return subMode switch
                {
                    2 => "TAKEOFF",
                    3 => "HOLD",
                    4 => "MISSION",
                    5 => "RTL",
                    6 => "LAND",
                    _ => "AUTO"
                };
            }

            return mainMode switch
            {
                1 => "MANUAL",
                2 => "ALTCTL",
                3 => "POSCTL",
                7 => "ACRO",
                8 => "OFFBOARD",
                _ => "IDLE"
            };
        }

        #endregion


        #region [7. 명령 응답 처리]

        // GCS가 보낸 명령에 대한 PX4 응답을 확인하고 실패한 명령만 로그로 남긴다.
        private void HookCommandAck(IClientDevice drone)
        {
            var commandClient = drone.GetMicroservice<ICommandClient>();
            if (commandClient == null) return;

            commandClient.OnCommandAck
                .Subscribe(ack =>
                {
                    string commandName = GetCommandName(ack.Command);
                    string resultText = GetCommandResultText(ack.Result);

                    if (ack.Result != MavResult.MavResultAccepted)
                    {
                        OnLogReceived?.Invoke($"[명령 경고] {commandName} -> {resultText}");
                    }
                })
                .AddTo(_subs);
        }

        // MAVLink 명령 코드를 사람이 읽기 쉬운 이름으로 변환한다.
        private string GetCommandName(MavCmd command)
        {
            return command switch
            {
                MavCmd.MavCmdNavTakeoff => "이륙",
                MavCmd.MavCmdNavReturnToLaunch => "복귀",
                MavCmd.MavCmdNavLand => "착륙",
                MavCmd.MavCmdNavWaypoint => "목적지 이동",
                _ => $"명령 {command}"
            };
        }

        // MAVLink 명령 결과 코드를 사람이 읽기 쉬운 결과 문구로 변환한다.
        private string GetCommandResultText(MavResult result)
        {
            return result switch
            {
                MavResult.MavResultAccepted => "수락됨",
                MavResult.MavResultTemporarilyRejected => "일시적 거절",
                MavResult.MavResultDenied => "거절됨",
                MavResult.MavResultUnsupported => "지원 안 됨",
                MavResult.MavResultFailed => "실행 실패",
                _ => $"알 수 없음 ({result})"
            };
        }

        #endregion


        #region [8. 연결 감시]

        // Heartbeat가 일정 시간 들어오지 않으면 연결 끊김으로 판단한다.
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
                        CheckHeartbeatTimeout();
                        await Task.Delay(200, _cts.Token);
                    }
                }
                catch (TaskCanceledException)
                {
                    // 서비스 종료 중 발생하는 정상 취소
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

        // 마지막 Heartbeat 수신 시간이 오래되었는지 확인한다.
        private void CheckHeartbeatTimeout()
        {
            double ageMs = (DateTime.UtcNow - _lastHeartbeatUtc).TotalMilliseconds;
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
        }

        #endregion
    }
}