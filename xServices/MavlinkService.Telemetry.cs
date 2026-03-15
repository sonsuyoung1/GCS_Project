using Asv.IO;
using Asv.Mavlink;
using Asv.Mavlink.Common;
using Asv.Mavlink.Minimal;
using R3;
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
        /// <summary>
        /// 드론의 실시간 센서 데이터(하트비트, GPS, 배터리, 기체 자세 등) 스트림을 구독하여 GCS 상태를 갱신합니다.
        /// </summary>
        private void HookTelemetry(IClientDevice drone)
        {
            if (_telemetryHooked) return;
            _telemetryHooked = true;

            OnLogReceived?.Invoke("[MAV] 센서 데이터(Telemetry) 스트림 연결 시작...");

            // 어떤 패킷이든 들어오면 살아있다는 방어 로직임.
            _router!.OnRxMessage
                    .Subscribe(_ =>
                    {
                        _lastHeartbeatUtc = DateTime.UtcNow; // 어떤 데이터든 들어오면 생존 시간 갱신

                        // 연결이 끊겨 있다가 다시 데이터가 들어오면 즉시 연결 복구
                        if (!CurrentState.IsConnected)
                        {
                            CurrentState.IsConnected = true;
                            OnDataUpdated?.Invoke(CurrentState);
                            OnLogReceived?.Invoke("[MAV] 통신 연결됨 (데이터 수신 확인)");
                        }
                    })
                    .AddTo(_subs);

            // 1-1) 하트비트 감시
            var hb = drone.GetMicroservice<IHeartbeatClient>();
            if (hb == null)
            {
                OnLogReceived?.Invoke("[MAV] [경고] 기체에서 하트비트 서비스를 찾을 수 없습니다.");
            }
            else
            {
                hb.RawHeartbeat
                    .Subscribe(packet =>
                    {
                        _lastHeartbeatUtc = DateTime.UtcNow; // 시간만 갱신! (여기서 IsConnected를 또 건드리면 UI 렉 발생)

                        bool isArmed = (packet?.BaseMode & MavModeFlag.MavModeFlagSafetyArmed) != 0;

                        // 만약 기존 상태랑 다르면? (시동이 방금 걸렸거나 꺼졌다면)
                        if (CurrentState.IsArmed != isArmed)
                        {
                            CurrentState.IsArmed = isArmed;
                            OnDataUpdated?.Invoke(CurrentState); // 데이터 변했다고 UI에 방송!
                        }
                    })
                    .AddTo(_subs);
                OnLogReceived?.Invoke("[MAV] 하트비트 감시 시작");
            }

            // 1-2) GCS 심장 박동(Heartbeat) 송신 타이머 (1초 간격)
            Observable.Timer(TimeSpan.Zero, TimeSpan.FromSeconds(1))
                .Subscribe(async _ =>
                {
                    try
                    {
                        if (_router == null) return;

                        // 관제탑(GCS) 전용 심장 박동 패킷 생성
                        var gcsHeartbeat = new HeartbeatPacket
                        {
                            SystemId = (byte)GcsSystemId,
                            ComponentId = (byte)GcsComponentId,
                        };

                        // 우리는 드론이 아니라 '관제탑'임을 명시
                        gcsHeartbeat.Payload.Type = MavType.MavTypeGcs;
                        gcsHeartbeat.Payload.Autopilot = MavAutopilot.MavAutopilotInvalid; // GCS는 오토파일럿이 아님
                        gcsHeartbeat.Payload.SystemStatus = MavState.MavStateActive;

                        // 공유기(Router)를 통해 드론에게 심장 박동 쏘기!
                        await _router.Send(gcsHeartbeat, _cts?.Token ?? CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        // 프로그램 종료 시점 등에서 발생하는 타이머 에러는 조용히 무시
                        System.Diagnostics.Debug.WriteLine($"[Heartbeat 전송 에러] {ex.Message}");
                    }
                })
                .AddTo(_subs); // 기존처럼 _subs에 넣어서 깔끔하게 생명주기 관리!
            OnLogReceived?.Invoke("[MAV] GCS 심장 박동 송신 시작 (1Hz)");


            // 2) 위치 정보 (GPS)
            drone.GetMicroservice<IPositionClient>()?.GlobalPosition
                .Subscribe(gp => {
                    if (gp == null) return;

                    CurrentState.Latitude = gp.Lat / 1e7;
                    CurrentState.Longitude = gp.Lon / 1e7;
                    CurrentState.Altitude = gp.Alt / 1000.0; // 고도(mm -> m 변환)
                    OnDataUpdated?.Invoke(CurrentState);
                }).AddTo(_subs);


            // 3) HUD 정보 (속도)
            drone.GetMicroservice<IPositionClient>()?.VfrHud
                .Subscribe(vfr => {
                    if (vfr == null) return;

                    CurrentState.Speed = Math.Round(vfr.Groundspeed, 2);
                    OnDataUpdated?.Invoke(CurrentState);
                }).AddTo(_subs);


            // 4) 배터리 정보
            drone.GetMicroservice<ITelemetryClient>()?.Battery
                .Subscribe(b => {
                    if (b == null) return;

                    CurrentState.BatteryPercent = b.BatteryRemaining;
                    OnDataUpdated?.Invoke(CurrentState);
                }).AddTo(_subs);


            // 5) 기체 자세 (Roll, Pitch, Yaw)
            _router!.OnRxMessage
                    .OfType<IProtocolMessage, AttitudePacket>()
                    .Subscribe(packet => {
                        CurrentState.Roll = packet.Payload.Roll * (180.0 / Math.PI);
                        CurrentState.Pitch = packet.Payload.Pitch * (180.0 / Math.PI);
                        CurrentState.Yaw = (packet.Payload.Yaw * (180.0 / Math.PI) + 360) % 360;
                        OnDataUpdated?.Invoke(CurrentState);
                    })
                    .AddTo(_subs);

            OnLogReceived?.Invoke("[MAV] 모든 데이터 스트림 파이프 연결 완료");
        }

        /// <summary>
        /// GCS가 전송한 명령에 대한 드론의 응답(수락/거절 사유)을 파싱하여 로깅합니다.
        /// </summary>
        /// <param name="drone"></param>
        private void HookCommandAck(IClientDevice drone)
        {
            var cmd = drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            // 수영님 스타일의 깔끔한 체이닝 적용
            cmd.OnCommandAck.Subscribe(ack =>
            {
                // 1. MAVLink 명령 번호를 알기 쉬운 한글로 번역 (자주 쓰는 것만)
                string commandName = ack.Command switch
                {
                    MavCmd.MavCmdNavTakeoff => "이륙 (Takeoff)",
                    MavCmd.MavCmdNavReturnToLaunch => "복귀 (RTL)",
                    MavCmd.MavCmdNavLand => "착륙 (Land)",
                    MavCmd.MavCmdNavWaypoint => "목적지 이동",
                    _ => $"명령 번호 {ack.Command}" // 모르는 건 번호로 출력
                };

                // 2. MAVLink 결과를 명확한 상태 메시지와 아이콘으로 번역
                string resultText = ack.Result switch
                {
                    MavResult.MavResultAccepted => "수락 및 실행됨",
                    MavResult.MavResultTemporarilyRejected => "일시적 거절 (드론 바쁨)",
                    MavResult.MavResultDenied => "거절됨 (조건 불만족)",
                    MavResult.MavResultUnsupported => "지원하지 않는 명령",
                    MavResult.MavResultFailed => "실행 실패",
                    _ => $"알 수 없음 ({ack.Result})"
                };

                // 3. 성공과 실패를 구분해서 로그 띄우기 (실패하면 [경고] 태그!)
                if (ack.Result == MavResult.MavResultAccepted)
                {
                    OnLogReceived?.Invoke($"[명령 응답] {commandName} -> {resultText}");
                }
                else
                {
                    OnLogReceived?.Invoke($"[명령 경고] {commandName} 실패 사유: {resultText}");
                }

            }).AddTo(_subs); // 잊지 않고 바구니에 담기!
        }

        /// <summary>
        /// 백그라운드에서 기체의 심장 박동(Heartbeat)을 지속적으로 감시하여 지정된 시간(3초) 응답이 없으면 통신 단절로 처리합니다.
        /// </summary>
        private void StartLinkWatchdog()
        {
            // 1. 중복 실행 방지 (완전히 끝나지 않은 요원이 있으면 패스)
            if (_linkWatchdogTask != null && !_linkWatchdogTask.IsCompleted) return;

            _lastHeartbeatUtc = DateTime.UtcNow;

            _linkWatchdogTask = Task.Run(async () =>
            {
                OnLogReceived?.Invoke("[시스템] 통신 상태 감시(Watchdog) 백그라운드 가동");

                try
                {
                    // CancellationToken이 유효한 동안만 무한 순찰
                    while (_cts != null && !_cts.Token.IsCancellationRequested)
                    {
                        var ageMs = (DateTime.UtcNow - _lastHeartbeatUtc).TotalMilliseconds;

                        // 2. 3초 이상 심장 박동(Heartbeat)이 없으면 연결 끊김으로 간주
                        if (ageMs > 3000 && CurrentState.IsConnected)
                        {
                            CurrentState.IsConnected = false;
                            OnDataUpdated?.Invoke(CurrentState);

                            // 조종사가 확 띄게 볼 수 있도록 비상 이모지 및 직관적 메시지 사용
                            OnLogReceived?.Invoke("[비상] 기체와의 통신이 끊겼습니다! (Heartbeat Timeout)");
                        }

                        // 0.2초마다 상태 확인 (만약 취소 명령이 오면 즉시 깨어남)
                        await Task.Delay(200, _cts.Token);
                    }
                }
                catch (TaskCanceledException)
                {
                    // StopAsync가 호출되어 정상적으로 감시가 취소된 경우 (조용히 무시)
                }
                catch (Exception ex)
                {
                    OnLogReceived?.Invoke($"[오류] 감시 프로세스 에러: {ex.Message}");
                }
                finally
                {
                    // 3. 핵심 버그 해결: 감시가 끝나면 반드시 요원을 초기화(null)하여 다음 연결을 준비!
                    _linkWatchdogTask = null;
                    OnLogReceived?.Invoke("[시스템] 통신 상태 감시(Watchdog) 종료");
                }
            });
        }

        // 아래 주석 친 부분이 yolo로 객체 탐지하여 마브링크 프로토콜로 받아 파싱하기 위한 로직임.
        /*private void HookCustomMessages(IProtocolRouter router)
        {
            // YOLO 타겟 메시지 (ID 250)
            router.OnRxMessage
                .OfType<IProtocolMessage, MavlinkMessage>()
                .Where(m => m.Id == 250)
                .Subscribe(m =>
                {
                    // 1. IPayload 상자를 가져옵니다.
                    var payload = m.GetPayload();

                    // 2. 상자의 크기만큼 빈 바이트 바구니(버퍼)를 준비합니다.
                    byte[] buffer = new byte[payload.GetMaxByteSize()];
                    var span = new Span<byte>(buffer);

                    // 3. 상자 안의 내용물(순수 바이트 데이터)을 바구니에 쏟아 붓습니다!
                    payload.Serialize(ref span);

                    // 4. 이제 완벽한 바이트 배열이 된 buffer를 파싱 메서드에 넘깁니다.
                    // (byte[]는 자동으로 ReadOnlySpan<byte>로 변환되어 찰떡같이 들어갑니다)
                    ParseYoloPacket(buffer);
                })
                .AddTo(_subs);
        }

        private void ParseYoloPacket(ReadOnlySpan<byte> data)
        {
            // 1단계 안전장치: 데이터 길이 검사 (방어적 프로그래밍)
            // ID(1) + Lat(4) + Lng(4) = 최소 9바이트가 필요합니다.
            // 통신 불량으로 5바이트만 왔는데 자르려고 하면 프로그램이 그 자리에서 뻗어버립니다(Crash).
            if (data.Length < 9)
            {
                OnLogReceived?.Invoke($"[경고] 잘못된 YOLO 패킷 수신 (길이: {data.Length})");
                return; // 데이터가 잘렸으면 과감하게 버립니다.
            }

            int id = data[0];

            // 2단계 안전장치: 네트워크 바이트 순서(Endian) 명시
            // BitConverter 대신 BinaryPrimitives를 사용하여 무조건 '리틀 엔디안'으로 읽도록 강제합니다.
            double lat = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(1, 4)) / 1E7;
            double lng = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(5, 4)) / 1E7;

            // 3단계 안전장치: UI 스레드 멈춤 방지 (BeginInvoke)
            // Invoke는 UI 업데이트가 끝날 때까지 통신 수신을 멈추고 기다립니다.
            // 통신이 1초에 수십 번씩 올 때는 BeginInvoke(비동기)를 써야 GCS 화면이 뚝뚝 끊기지 않습니다.
            Application.Current.Dispatcher.BeginInvoke(() => {
                var target = DetectedTargets.FirstOrDefault(t => t.ID == id);
                bool isTracked = target?.IsBeingTracked ?? false;

                if (target != null)
                {
                    target.Lat = lat; target.Lng = lng;
                }
                else
                {
                    DetectedTargets.Add(new DetectedTarget { ID = id, Lat = lat, Lng = lng, TargetType = "Person" });
                }
                OnTargetLocationUpdated?.Invoke(id, lat, lng, "Person", isTracked);
            });
        }*/
        #endregion
    }
}
