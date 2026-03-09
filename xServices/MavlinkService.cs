using Asv.Cfg;
using Asv.IO;
using Asv.Mavlink;
using Asv.Mavlink.Common;
using Asv.Mavlink.Minimal;
using GCS_Project.Models;
using ObservableCollections;
using R3;
using System.Windows;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.Metrics;
using System.Net.Sockets;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace GCS_Project.xServices
{
    public class MavlinkService : IAsyncDisposable
    {
        #region [1. 필드 및 설정 (Core Fields)]
        private readonly List<GeoPoint> _waypoints = new();
        private bool _isMissionRunning;
        private CancellationTokenSource? _cts;
        private readonly CompositeDisposable _subs = new(); // 메모리 누수 방지하기 위해 구독 해지 쓰레기통
        private const string GcsRouterId = "GCS_ROUTER";
        private const int GcsSystemId = 255;
        private const int GcsComponentId = 190;
        private bool _telemetryHooked;
        private bool _linkDownReported;
        private DateTime _lastHeartbeatUtc = DateTime.MinValue;
        private Task? _linkWatchdogTask;


        // v4 core
        private IProtocolFactory? _protocol;
        private IProtocolRouter? _router;
        private IDeviceExplorer? _explorer;
        private IClientDevice? _drone;
        #endregion


        #region [2. 데이터 속성 및 상태 (State & Data)]
        public DroneState CurrentState { get; } = new DroneState();
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
        public async Task StartAsync(string listenHost = "0.0.0.0", int listenPort = 14550, string droneIp = "192.168.1.136") // ip는 와이파이 달라질 때마다 바꿔면 됨
        {
            if (_router != null) return;

            OnLogReceived?.Invoke("[시스템] MAVLink 서비스 시작 중...");

            _cts = new CancellationTokenSource();

            // 1) 프로토콜 엔진 생성
            _protocol = Protocol.Create(builder =>
            {
                builder.RegisterMavlinkV2Protocol();
                builder.Features.RegisterBroadcastFeature<MavlinkMessage>();
                builder.Formatters.RegisterSimpleFormatter();
            });
            OnLogReceived?.Invoke("[MAV] 프로토콜 생성됨");

            // 2) 라우터 설정 (데이터 통로)
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
            OnLogReceived?.Invoke("[MAV] 라우터 생성됨");

            // GCS →  PX4 (Telemetry 수신)
            _router.AddUdpPort(p =>
            {
                p.Scheme = "udp";
                p.Host = droneIp;
                p.Port = 14551;
                p.IsEnabled = true;
                p.Name = "udp_out";
            });


            // 3) 장치 탐색기 설정 (드론 자동 찾기)
            var seq = new PacketSequenceCalculator(); // sequence는 몇 번쨰 피킷인지 순서를 적어 보내는 역할
            var identity = new MavlinkIdentity(GcsSystemId, GcsComponentId); // identity는 gcs 내가 누군지 알려주는 역할
            _explorer = DeviceExplorer.Create(_router, b =>
            {
                b.SetConfig(new ClientDeviceBrowserConfig
                {
                    DeviceTimeoutMs = 1000,
                    DeviceCheckIntervalMs = 30_000 // 30초 유예기간 (핵심)
                });
                b.Factories.RegisterDefaultDevices(identity, seq, new InMemoryConfiguration());
            });
            OnLogReceived?.Invoke("[MAV] DeviceExplorer가 생성되었습니다");


            // 4) 드론 장치 발견 대기
            var found = new TaskCompletionSource();
            _subs.Add(_explorer.Devices.ObserveAdd().Take(1).Subscribe(kvp => // 새로운 기기가 추가되는걸 Observe 지켜보다가 take(1)으로 발견되면 코드 실행
            {
                _drone = kvp.Value.Value;
                found.TrySetResult();
            }));
            await found.Task;
            OnLogReceived?.Invoke("[시스템] 드론 장치 감지됨");


            // 5) 드론 상태 준비 완료 대기
            var ready = new TaskCompletionSource();
            _subs.Add(_drone!.State.Subscribe(st =>
            {
                if (st == ClientDeviceState.Complete) ready.TrySetResult();
            }));
            await ready.Task;
            

            // 6) 데이터 스트림 연결
            HookTelemetry(_drone);
            OnLogReceived?.Invoke("[MAV] 텔레메트리가 연결됨");
            //HookCustomMessages(_router); // YOLO 전용
            HookCommandAck(_drone);
            StartLinkWatchdog();

            OnLogReceived?.Invoke("[MAV] 장치 연결됨 (device ready)");
        }

        /// <summary>
        /// 활성화된 모든 MAVLink 통신 연결과 비동기 작업을 안전하게 종료하고, 메모리 누수 방지를 위해 리소스를 해제합니다.
        /// </summary>
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
                    CurrentState.Latitude = gp.Lat / 1e7;
                    CurrentState.Longitude = gp.Lon / 1e7;
                    CurrentState.Altitude = gp.Alt / 1000.0; // 고도(mm -> m 변환)
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
        #endregion


        #region [6. 비행 명령 (Flight Commands)]
        /// <summary>
        /// 드론의 비행 모드(GUIDED, AUTO, RTL 등)를 변경합니다.
        /// </summary>
        public async Task SetFlightMode(MavModeFlag baseMode, uint customMode)
        {
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 기체가 연결되지 않아 모드를 변경할 수 없습니다.");
                return;
            }

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            try
            {
                // 최신 PX4가 좋아하는 CommandLong 방식으로 모드 변경!
                await cmd.SendCommandLong(
                    MavCmd.MavCmdDoSetMode,
                    (float)baseMode,       // Param 1: 기본 모드 (보통 CustomModeEnabled = 1)
                    customMode,            // Param 2: 커스텀 모드 번호 (PX4의 AUTO = 4)
                    0f, 0f, 0f, 0f, 0f,    // 나머지 파라미터는 0
                    _cts?.Token ?? CancellationToken.None
                );

                OnLogReceived?.Invoke($"[명령] 비행 모드 변경 요청 전송 (커스텀 코드: {customMode})");
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] 모드 변경 중 예외 발생: {ex.Message}");
            }
        }

        /// <summary>
        /// 지정된 고도로 이륙 명령(Takeoff)을 전송합니다.
        /// </summary>
        public async Task Takeoff(float alt)
        {
            // 1. 안전장치: 드론 객체와 통신 상태(IsConnected) 꼼꼼하게 확인
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 기체가 연결되지 않아 이륙할 수 없습니다.");
                return;
            }

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            // 2. 명령 전송 및 에러 방어막
            try
            {
                OnLogReceived?.Invoke($"[명령] 이륙 시도 (목표 고도: {alt}m)...");

                await SetFlightMode(MavModeFlag.MavModeFlagCustomModeEnabled, 4);

                // 드론이 모드를 바꿀 수 있게 잠깐 숨 돌릴 시간(0.2초)을 줍니다.
                await Task.Delay(200);

                // 실제 이륙 명령 전송 (빈칸은 반드시 NaN으로 채움!)
                // param7 = altitude, 나머지 1~6은 NaN(미사용) 처리
                await cmd.SendCommandLong(
                    MavCmd.MavCmdNavTakeoff,
                    float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN,
                    alt,
                    _cts?.Token ?? CancellationToken.None
                );

                OnLogReceived?.Invoke($"[확인] 이륙(Takeoff) 명령이 기체로 전송되었습니다.");
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

        /// <summary>
        /// 비행 중 실시간으로 기체의 목표 이동 속도를 변경합니다.
        /// </summary>
        public async Task ChangeTargetSpeed(float targetSpeed)
        {
            // 안전장치 확인
            if (_drone == null || !CurrentState.IsConnected) return;

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            try
            {
                // MavCmdDoChangeSpeed 전송
                // Param1: 1 (Ground Speed, 멀티로터용)
                // Param2: 목표 속도 (m/s)
                // Param3: Throttle (-1은 변경 안 함 유지)
                await cmd.SendCommandLong(
                    MavCmd.MavCmdDoChangeSpeed,
                    1f, targetSpeed, -1f, 0f, 0f, 0f, 0f,
                    _cts?.Token ?? CancellationToken.None
                );

                OnLogReceived?.Invoke($"[명령] 목표 속도를 {targetSpeed}m/s로 변경했습니다.");
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] 속도 변경 명령 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 비행 중 실시간으로 목표 고도를 변경합니다. (제자리 고도 상승/하강)
        /// </summary>
        public async Task ChangeTargetAltitude(float targetAltitude)
        {
            // 안전장치 확인
            if (_drone == null || !CurrentState.IsConnected) return;

            try
            {
                await GoTo(CurrentState.Latitude, CurrentState.Longitude, targetAltitude);

                // (GoTo 내부에 이미 로그가 출력되므로, 여기서는 별도의 로그를 찍지 않아도 깔끔합니다)
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] 고도 변경 명령 실패: {ex.Message}");
            }
        }
        #endregion


        #region [7. 미션 로직 (Mission & Waypoints)]
        /// <summary>
        /// 등록된 웨이포인트를 순차적으로 방문하는 자율 비행 임무를 시작합니다.
        /// </summary>
        public void StartMission()
        {
            // 1. 사전 검사: 임무 수행 가능 여부 확인
            if (_isMissionRunning || _waypoints.Count == 0 || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 임무 시작 불가: 웨이포인트가 없거나 연결 상태를 확인하세요.");
                return;
            }

            _isMissionRunning = true;

            Task.Run(async () =>
            {
                try
                {
                    OnLogReceived?.Invoke($"[미션] 웨이포인트 비행 시작 (남은 지점: {_waypoints.Count}개)");

                    // 2. 비행 모드 변경: 컴퓨터 명령을 듣는 AUTO(4) 모드로 전환
                    await SetFlightMode(MavModeFlag.MavModeFlagCustomModeEnabled, 4);
                    await Task.Delay(500, _cts?.Token ?? CancellationToken.None);

                    // 현재 드론이 향하고 있는 '지점'을 추적하기 위한 변수
                    GeoPoint currentDestination = null;

                    // 3. 임무 루프: 리스트에 방문할 점이 남아있는 동안 반복
                    while (_isMissionRunning && _waypoints.Count > 0)
                    {
                        if (_cts != null && _cts.IsCancellationRequested) break;

                        // 리스트의 가장 앞(0번)에 있는 지점을 '이번에 가야 할 곳'으로 설정
                        var nextPoint = _waypoints[0];

                        // 핵심 로직: 새로운 지점으로 목적지가 바뀌었을 때만 GoTo 명령 전송 (중복 전송 방지)
                        if (currentDestination != nextPoint)
                        {
                            await GoTo(nextPoint.Lat, nextPoint.Lng, (float)CurrentState.TargetAltitude);
                            currentDestination = nextPoint;

                            OnLogReceived?.Invoke($"[미션] 다음 웨이포인트로 이동 중... (위도: {Math.Round(nextPoint.Lat, 5)}, 경도: {Math.Round(nextPoint.Lng, 5)})");
                        }

                        // 4. 도착 판정: 드론이 해당 지점의 일정 반경(예: 3m) 안에 들어왔는지 확인
                        if (IsReached(nextPoint))
                        {
                            // 핵심 방어막: UI 관리자를 부르지 않고 lock을 걸어 안전하게 삭제합니다!
                            lock (_waypoints)
                            {
                                if (_waypoints.Count > 0)
                                {
                                    _waypoints.RemoveAt(0); // 도달한 지점은 리스트에서 제거
                                }
                            }

                            // 지운 다음 뷰모델에게 "개수 바뀌었어!"라고 이벤트만 던지면, 
                            // 뷰모델이 알아서 화면(UI)을 갱신해 줍니다!
                            OnWaypointCountChanged?.Invoke(_waypoints.Count);

                            OnLogReceived?.Invoke($"[미션] 웨이포인트 도달 완료. (남은 지점: {_waypoints.Count}개)");
                        }

                        // 1초 간격으로 상태 체크 (통신 과부하 방지)
                        await Task.Delay(1000, _cts?.Token ?? CancellationToken.None);
                    }

                    // 5. 종료 결과 보고
                    if (_waypoints.Count == 0)
                    {
                        OnLogReceived?.Invoke("[미션] 모든 예약된 지점을 방문하였습니다.");
                        OnMissionCompleted?.Invoke();
                    }
                    else
                    {
                        OnLogReceived?.Invoke("[미션] 사용자에 의해 임무가 중단되었습니다.");
                    }
                }
                catch (TaskCanceledException)
                {
                    OnLogReceived?.Invoke("[미션] 시스템 종료로 임무가 중단되었습니다.");
                }
                catch (Exception ex)
                {
                    OnLogReceived?.Invoke($"[오류] 미션 루프 에러: {ex.Message}");
                }
                finally
                {
                    _isMissionRunning = false;
                }
            });
        }


        /// <summary>
        /// 지정된 위도, 경도, 고도로 기체를 즉시 이동시킵니다.
        /// </summary>
        public async Task GoTo(double lat, double lng, float alt)
        {
            // 1. 안전장치: 드론 객체와 통신 상태 확인
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 기체가 연결되지 않아 이동 명령(GoTo)을 수행할 수 없습니다.");
                return;
            }

            // 2. 명령 하달기(CommandClient) 꺼내기
            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            try
            {
                OnLogReceived?.Invoke($"[명령] 웨이포인트 이동 지시 ({lat:F5}, {lng:F5}, 고도: {alt}m)...");

                // 3. PX4 맞춤형 "해당 좌표로 즉시 이동하라(Reposition)" 명령 전송!
                await cmd.SendCommandInt(
                    MavCmd.MavCmdDoReposition,
                    MavFrame.MavFrameGlobalRelativeAltInt,
                    false,       // 추가됨: current (단발성 명령이므로 false)
                    false,       // 추가됨: autocontinue (자동 다음 명령 실행 여부, false)
                    -1f,         // Param1: 이동 속도 (-1을 넣으면 드론의 기본 속도 사용)
                    0f,          // Param2: 비트마스크 (기본값 0)
                    0f,          // Param3: 예약됨 (0)
                    float.NaN,   // Param4: Yaw (NaN이면 목적지를 바라보며 비행)
                    (int)(lat * 1E7), // X: 위도 (10의 7승 곱하기)
                    (int)(lng * 1E7), // Y: 경도 (10의 7승 곱하기)
                    alt,              // Z: 고도
                    _cts?.Token ?? CancellationToken.None
                );
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] GoTo 명령 전송 실패: {ex.Message}");
            }
        }


        /// <summary>
        /// 드론이 현재 목표 지점(Target)의 허용 범위 내에 도달했는지 판정합니다.
        /// </summary>
        private bool IsReached(GeoPoint destination)
        {
            // 1. 지구의 평균 반지름 (미터 단위)
            const double R = 6371000.0;

            // 2. 도(Degree)를 라디안(Radian)으로 변환
            double lat1 = CurrentState.Latitude * Math.PI / 180.0;
            double lon1 = CurrentState.Longitude * Math.PI / 180.0;
            double lat2 = destination.Lat * Math.PI / 180.0;
            double lon2 = destination.Lng * Math.PI / 180.0;

            // 3. 위도와 경도의 차이
            double dLat = lat2 - lat1;
            double dLon = lon2 - lon1;

            // 4. 하버사인(Haversine) 공식 적용 (곡면 거리 계산)
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1) * Math.Cos(lat2) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

            // 5. 실제 드론과 목적지 사이의 오차 없는 거리 (미터)
            double distanceInMeters = R * c;

            // 6. 오차 범위 3m 이내면 도착으로 판정!
            return distanceInMeters <= 3.0;
        }

        /// <summary>
        /// 진행 중인 자율 비행 임무를 즉시 중단하고 기체를 제자리에 정지시킵니다.
        /// </summary>
        /// <summary>
        /// 진행 중인 자율 비행 임무를 즉시 중단하고 기체를 제자리에 정지(Hovering)시킵니다.
        /// </summary>
        public async Task StopMission()
        {
            // 이미 멈춰있으면 무시
            if (!_isMissionRunning) return;

            // 1. 소프트웨어 미션 루프 중단
            _isMissionRunning = false;

            OnLogReceived?.Invoke("[미션] 임무가 중단되었습니다. 기체를 제동합니다!");

            // 2. 하드웨어(드론) 급브레이크! 
            // 방금 전 완성한 완벽한 GoTo 엔진을 사용해 "너의 지금 위치로 당장 이동해!"라고 명령합니다.
            if (CurrentState.IsConnected)
            {
                try
                {
                    await GoTo(CurrentState.Latitude, CurrentState.Longitude, (float)CurrentState.Altitude);
                    OnLogReceived?.Invoke("[명령] 제자리 정지(Hovering) 명령 전송 완료");
                }
                catch (Exception ex)
                {
                    OnLogReceived?.Invoke($"[오류] 정지 명령 실패: {ex.Message}");
                }
            }
        }


        /// <summary>
        /// 지도에 새로운 웨이포인트(경유지) GPS 좌표를 추가합니다.
        /// </summary>
        public void AddWaypoint(double lat, double lng)
        {
            // 1. 방어 코딩: 유효하지 않은 GPS 좌표 필터링
            if (lat < -90 || lat > 90 || lng < -180 || lng > 180)
            {
                OnLogReceived?.Invoke($"[경고] 유효하지 않은 GPS 좌표입니다. (Lat: {lat}, Lng: {lng})");
                return;
            }

            _waypoints.Add(new GeoPoint { Lat = lat, Lng = lng });
            OnWaypointCountChanged?.Invoke(_waypoints.Count);

            // 2. UX 강화: 점이 정상적으로 추가되었음을 로그로 출력
            OnLogReceived?.Invoke($"[미션] 웨이포인트 추가됨 (총 {_waypoints.Count}개)");
        }

        /// <summary>
        /// 실수로 찍은 마지막 웨이포인트를 취소(삭제)합니다.
        /// </summary>
        public void UndoWaypoint()
        {
            if (_waypoints.Count > 0)
            {
                // 리스트의 맨 마지막 요소(가장 최근에 찍은 점)를 제거합니다.
                _waypoints.RemoveAt(_waypoints.Count - 1);

                // UI 화면에 웨이포인트 개수가 줄어들었다고 알려줍니다.
                OnWaypointCountChanged?.Invoke(_waypoints.Count);

                OnLogReceived?.Invoke($"[미션] 마지막 웨이포인트 취소 (남은 개수: {_waypoints.Count}개)");
            }
            else
            {
                OnLogReceived?.Invoke("[경고] 취소할 웨이포인트가 없습니다.");
            }
        }

        /// <summary>
        /// 등록된 모든 웨이포인트를 싹 다 지우고 초기화합니다.
        /// </summary>
        public void ClearWaypoints()
        {
            if (_waypoints.Count > 0)
            {
                // 리스트를 완전히 비웁니다.
                _waypoints.Clear();

                // UI 화면에 개수가 0이 되었다고 알려줍니다.
                OnWaypointCountChanged?.Invoke(0);

                OnLogReceived?.Invoke("[미션] 모든 웨이포인트를 삭제했습니다.");
            }
            else
            {
                // 이미 비어있을 때 누르면 조용히 알려주는 센스
                OnLogReceived?.Invoke("[알림] 이미 웨이포인트가 비어있습니다.");
            }
        }
        #endregion


        #region [8. 자원 해제 (Dispose)]
        public async ValueTask DisposeAsync() => await StopAsync();
        #endregion


        #region [9. 수동 조종 (Virtual Joystick)]

        // 조이스틱의 4가지 축 값 (기본값 세팅)
        // X, Y, R은 -1000 ~ 1000 / Z(스로틀)는 0 ~ 1000 (500이 제자리 호버링)
        private short _joyX = 0;   // 앞/뒤 (Pitch)
        private short _joyY = 0;   // 좌/우 (Roll)
        private short _joyZ = 500; // 상승/하강 (Thrust)
        private short _joyR = 0;   // 좌우 회전 (Yaw)

        private bool _isManualControlActive = false;
        private Task? _manualControlTask;

        /// <summary>
        /// 키보드나 패드에서 입력받은 조이스틱 값을 업데이트합니다.
        /// </summary>
        public void SetJoystick(short pitch, short roll, short thrust, short yaw)
        {
            _joyX = pitch;
            _joyY = roll;
            _joyZ = thrust;
            _joyR = yaw;
        }

        /// <summary>
        /// 1초에 20번씩 드론에게 가상 조이스틱 상태를 전송하는 엔진을 켭니다.
        /// </summary>
        public void StartManualControl()
        {
            if (_manualControlTask != null && !_manualControlTask.IsCompleted) return;

            _isManualControlActive = true;
            OnLogReceived?.Invoke("[시스템] 수동 조종(가상 조이스틱) 엔진 가동!");

            _manualControlTask = Task.Run(async () =>
            {
                while (_isManualControlActive && _cts != null && !_cts.IsCancellationRequested)
                {
                    if (_drone != null && CurrentState.IsConnected && _router != null)
                    {
                        try
                        {
                            // 수동 조종 패킷 조립
                            var pkt = new ManualControlPacket();
                            pkt.Payload.Target = 1; // 보통 1번 드론
                            pkt.Payload.X = _joyX;
                            pkt.Payload.Y = _joyY;
                            pkt.Payload.Z = _joyZ;
                            pkt.Payload.R = _joyR;
                            pkt.Payload.Buttons = 0;

                            // 전송! (1초에 20번 쏘기 때문에 로그는 남기지 않습니다)
                            await _router.Send(pkt, _cts.Token);
                        }
                        catch { /* 통신 튐 무시 */ }
                    }
                    // 50ms 대기 (1초에 약 20번 전송 = 20Hz)
                    await Task.Delay(50);
                }
            });
        }

        /// <summary>
        /// 수동 조종 엔진을 끕니다.
        /// </summary>
        public void StopManualControl()
        {
            _isManualControlActive = false;
            _manualControlTask = null;
            OnLogReceived?.Invoke("[시스템] 수동 조종 엔진 종료.");
        }
        #endregion
    }
}