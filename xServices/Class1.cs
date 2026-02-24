using Asv.Cfg;
using Asv.IO;
using Asv.Mavlink;
using Asv.Mavlink.Common;
using GCS_Project.Models;
using ObservableCollections;
using R3;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;

namespace GCS_Project.xServices
{
    public class calss1 : IAsyncDisposable
    {
        #region [1. 필드 및 설정 (Core Fields)]
        private readonly List<GeoPoint> _waypoints = new();
        private bool _isMissionRunning;
        private CancellationTokenSource? _cts;
        private readonly CompositeDisposable _subs = new();
        private const string GcsRouterId = "GCS_ROUTER";
        private const int GcsSystemId = 255;
        private const int GcsComponentId = 255;

        // v4 core
        private IProtocolFactory? _protocol;
        private IProtocolRouter? _router;
        private IDeviceExplorer? _explorer;
        private IClientDevice? _drone;

        // 아래 두 줄이 추가됨
        private DateTime _lastHeartbeatUtc = DateTime.MinValue;
        private Task? _linkWatchdogTask;
        #endregion

        #region [2. 데이터 속성 및 상태 (State & Data)]
        public DroneState CurrentState { get; } = new DroneState();
        // public ObservableCollection<DetectedTarget> DetectedTargets { get; } = new();
        #endregion

        #region [3. 이벤트 (Events)]
        public event Action<DroneState>? OnDataUpdated;
        public event Action<string>? OnLogReceived;
        public event Action<int>? OnWaypointCountChanged;
        public event Action? OnMissionCompleted;
        #endregion

        #region [4. 초기화 및 서비스 제어 (Lifecycle)]
        /// <summary>
        /// MAVLink 프로토콜과 UDP 라우터를 초기화하여 기체를 탐색하고, 연결이 완료되면 데이터 스트림을 활성화합니다.
        /// </summary>
        /// <param name="listenHost"></param>
        /// <param name="listenPort"></param>
        /// <param name="droneIp"></param>
        /// <returns></returns>
        public async Task StartAsync(string listenHost = "0.0.0.0", int listenPort = 14550, string droneIp = "192.168.1.136")
        {
            if (_router != null) return;

            OnLogReceived?.Invoke("[시스템] MAVLink 서비스 시작 중...");

            _cts = new CancellationTokenSource();

            // 1. 프로토콜 엔진 생성
            _protocol = Protocol.Create(builder =>
            {
                builder.RegisterMavlinkV2Protocol();
                builder.Features.RegisterBroadcastFeature<MavlinkMessage>();
                builder.Formatters.RegisterSimpleFormatter();
            });
            OnLogReceived?.Invoke("[MAV] 프로토콜 생성");

            // 2. 라우터 설정 (데이터 통로)
            // PX4 → GCS (Telemetry 수신)
            _router = _protocol.CreateRouter(GcsRouterId);
            _router.AddUdpPort(p =>
            {
                p.Scheme = "udp";
                p.Host = listenHost;
                p.Port = listenPort;
                p.IsEnabled = true;
                p.Name = "udp_in";
            });
            OnLogReceived?.Invoke("[MAV] Router created");

            // GCS →  PX4 (Telemetry 수신)
            _router.AddUdpPort(p =>
            {
                p.Scheme = "udp";
                p.Host = droneIp; // 메서드 매개변수인 droneIp("192.168.0.6") 사용
                p.Port = 14552;
                p.IsEnabled = true;
                p.Name = "udp_out";
            });

            // 3. 장치 탐색기 설정 (드론 자동 찾기)
            var seq = new PacketSequenceCalculator();
            var identity = new MavlinkIdentity(GcsSystemId, GcsComponentId);

            _explorer = DeviceExplorer.Create(_router, b =>
            {
                b.SetConfig(new ClientDeviceBrowserConfig { DeviceTimeoutMs = 1000 });
                b.Factories.RegisterDefaultDevices(identity, seq, new InMemoryConfiguration());
            });
            OnLogReceived?.Invoke("[MAV] DeviceExplorer가 생성되었습니다");

            // 4. 드론 장치 발견 대기
            var found = new TaskCompletionSource();
            _subs.Add(_explorer.Devices.ObserveAdd().Take(1).Subscribe(kvp =>
            {
                _drone = kvp.Value.Value;
                found.TrySetResult();
            }));

            await found.Task;
            OnLogReceived?.Invoke("[시스템] 드론 장치 감지됨");


            // 5. 드론 상태 준비 완료 대기
            var ready = new TaskCompletionSource();
            _subs.Add(_drone!.State.Subscribe(st =>
            {
                if (st == ClientDeviceState.Complete) ready.TrySetResult();
            }));

            await ready.Task;

            // 6. 데이터 스트림 연결
            HookTelemetry(_drone);
            OnLogReceived?.Invoke("[MAV] 텔레메트리가 연결됨");

            HookCommandAck(_drone);
            StartLinkWatchdog();

            OnLogReceived?.Invoke("[MAV] 장치 연결됨 (device ready)");
        }

        /// <summary>
        /// 활성화된 모든 MAVLink 통신 연결과 비동기 작업을 안전하게 종료하고, 메모리 누수 방지를 위해 리소스를 해제합니다.
        /// </summary>
        /// <returns></returns>
        public async Task StopAsync()
        {
            _isMissionRunning = false;

            _subs.Dispose();

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            if (_explorer is IAsyncDisposable ax1) await ax1.DisposeAsync();
            _explorer = null;

            if (_router is IAsyncDisposable ax2) await ax2.DisposeAsync();
            _router = null;

            _protocol = null;
            _drone = null;

            CurrentState.IsConnected = false;
            OnDataUpdated?.Invoke(CurrentState);
            OnLogReceived?.Invoke("[MAV] 연결 종료됨");
        }
        #endregion

        #region [5. 텔레메트리 핸들링 (Telemetry Hooks)]
        /// <summary>
        /// 드론의 실시간 센서 데이터(하트비트, GPS, 배터리, 기체 자세 등) 스트림을 구독하여 GCS 상태를 갱신합니다.
        /// </summary>
        /// <param name="drone"></param>
        private void HookTelemetry(IClientDevice drone)
        {
            OnLogReceived?.Invoke("[MAV] 센서 데이터(Telemetry) 스트림 연결 시작...");

            // 1) 하트비트 (팀원의 도배 방지 로직 적용!)
            drone.GetMicroservice<IHeartbeatClient>()?.RawHeartbeat
                .Subscribe(_ =>
                {
                    // 나중에 연결 끊김 감지를 위해 시간 기록 (팀원 아이디어)
                    // _lastHeartbeatUtc = DateTime.UtcNow; // (뷰모델에 변수가 있다면 주석 해제)

                    if (!CurrentState.IsConnected)
                    {
                        CurrentState.IsConnected = true;
                        OnDataUpdated?.Invoke(CurrentState);
                        OnLogReceived?.Invoke("[MAV] 드론 하트비트 수신 완료! (통신 100% 정상)");
                    }
                })
                .AddTo(_subs);

            // 2) 위치 정보 (GPS)
            drone.GetMicroservice<IPositionClient>()?.GlobalPosition
                .Subscribe(gp => {
                    // 라이브러리가 이미 변환해 주므로 1e7 나누기 생략 (이상하면 1e7로 나누세요)
                    CurrentState.Latitude = gp.Lat;
                    CurrentState.Longitude = gp.Lon;
                    CurrentState.Altitude = gp.Alt;
                    OnDataUpdated?.Invoke(CurrentState);
                }).AddTo(_subs);

            // 3) HUD 정보 (속도)
            drone.GetMicroservice<IPositionClient>()?.VfrHud
                .Subscribe(vfr => {
                    CurrentState.Speed = Math.Round(vfr.Groundspeed, 2);
                    OnDataUpdated?.Invoke(CurrentState);
                }).AddTo(_subs);

            // 4) 배터리 정보
            drone.GetMicroservice<ITelemetryClient>()?.Battery
                .Subscribe(b => {
                    CurrentState.BatteryPercent = b.BatteryRemaining;
                    OnDataUpdated?.Invoke(CurrentState);
                }).AddTo(_subs);

            // 5) 기체 자세 (Roll, Pitch, Yaw)
            _router!.OnRxMessage
                    .OfType<IProtocolMessage, AttitudePacket>()
                    .Subscribe(packet => {
                        CurrentState.Roll = packet.Payload.Roll * (180.0 / Math.PI);
                        CurrentState.Pitch = packet.Payload.Pitch * (180.0 / Math.PI);
                        CurrentState.Heading = (packet.Payload.Yaw * (180.0 / Math.PI) + 360) % 360;

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
        /// // 백그라운드에서 기체의 심장 박동(Heartbeat)을 지속적으로 감시하여, 지정된 시간(3초) 응답이 없으면 통신 단절로 처리합니다.
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
        #endregion

        #region [6. 비행 명령 제어 (Flight Commands)]
        /// <summary>
        /// 지정된 고도로 이륙 명령(Takeoff)을 전송합니다.
        /// </summary>
        /// <param name="altitude">목표 이륙 고도 (미터)</param>
        public async Task Takeoff(float altitude)
        {
            // 1. 안전장치: 드론 객체와 통신 상태(IsConnected)까지 꼼꼼하게 확인
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 기체가 연결되지 않아 이륙할 수 없습니다.");
                return;
            }

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            // 2. 명령 전송 및 에러 방어막 (try-catch)
            try
            {
                OnLogReceived?.Invoke($"[명령] 이륙 시도 (목표 고도: {altitude}m)...");

                await cmd.SendCommandLong(
                    MavCmd.MavCmdNavTakeoff,
                    0f, 0f, 0f, 0f, 0f, 0f, altitude, // altitude는 이미 float이므로 그대로 둠
                    _cts?.Token ?? CancellationToken.None
                );
            }
            catch (Exception ex)
            {
                // 통신 지연이나 에러 발생 시 프로그램이 안 튕기도록 조용히 로그만 출력
                OnLogReceived?.Invoke($"[오류] 이륙 명령 전송 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 현재 위치에서 즉시 착륙 명령(Land)을 전송합니다.
        /// </summary>
        public async Task Landing()
        {
            // 1. 안전장치
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 기체가 연결되지 않아 착륙 명령을 내릴 수 없습니다.");
                return;
            }

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            // 2. 명령 전송 및 에러 방어막
            try
            {
                OnLogReceived?.Invoke(" [명령] 착륙 시도...");

                await cmd.SendCommandLong(
                    MavCmd.MavCmdNavLand,
                    0f, 0f, 0f, 0f, 0f, 0f, 0f,
                    _cts?.Token ?? CancellationToken.None
                );
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] 착륙 명령 전송 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 기체를 이륙했던 위치(Home)로 되돌아오게 하는 복귀 명령(RTL)을 전송합니다.
        /// </summary>
        public async Task Rtl()
        {
            // 1. 안전장치
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 기체가 연결되지 않아 복귀(RTL) 명령을 내릴 수 없습니다.");
                return;
            }

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            // 2. 명령 전송 및 에러 방어막
            try
            {
                OnLogReceived?.Invoke("[명령] 드론 복귀(RTL) 시도 중...");

                await cmd.SendCommandLong(
                    MavCmd.MavCmdNavReturnToLaunch,
                    0f, 0f, 0f, 0f, 0f, 0f, 0f,
                    _cts?.Token ?? CancellationToken.None
                );
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] 복귀(RTL) 명령 전송 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 기체의 모터를 회전시키는 시동(Arm) 명령을 전송합니다.
        /// </summary>
        public async Task SendArmAsync()
        {
            // 1. 방탄 안전장치 (통일)
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 통신이 끊겨 시동(Arm)을 걸 수 없습니다.");
                return;
            }

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            // 2. 에러 방어막 및 전송
            try
            {
                OnLogReceived?.Invoke("[명령] 기체 시동(Arm) 전송 중...");

                // 파라미터 1번 = 1 (Arm)
                await cmd.SendCommandLong(
                    MavCmd.MavCmdComponentArmDisarm,
                    1f, 0f, 0f, 0f, 0f, 0f, 0f,
                    _cts?.Token ?? CancellationToken.None
                );
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] 시동(Arm) 명령 전송 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 기체의 모터를 정지시키는 시동 끄기(Disarm) 명령을 전송합니다.
        /// </summary>
        public async Task SendDisarmAsync()
        {
            // 1. Disarm에도 IsConnected 안전장치 추가!
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 통신이 끊겨 시동을 끌 수(Disarm) 없습니다.");
                return;
            }

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            // 2. 에러 방어막 및 전송
            try
            {
                OnLogReceived?.Invoke("[명령] 기체 시동 끄기(Disarm) 전송 중...");

                // 파라미터 1번 = 0 (Disarm)
                await cmd.SendCommandLong(
                    MavCmd.MavCmdComponentArmDisarm,
                    0f, 0f, 0f, 0f, 0f, 0f, 0f,
                    _cts?.Token ?? CancellationToken.None
                );
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] 시동 끄기(Disarm) 명령 전송 실패: {ex.Message}");
            }
        }
        #endregion

        #region [8. 자원 해제 (Dispose)]
        public async ValueTask DisposeAsync() => await StopAsync();
        #endregion
    }
}