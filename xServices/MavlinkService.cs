using Asv.Cfg;
using Asv.IO;
using Asv.Mavlink;
using Asv.Mavlink.Common;
using Asv.Mavlink.Minimal;
using GCS_Project.Models;
using ObservableCollections;
using R3;
using System;
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
        private const int GcsComponentId = 255;
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
        public async Task StartAsync(string listenHost = "0.0.0.0", int listenPort = 14550, string droneIp = "192.168.0.46") // ip는 와이파이 달라질 때마다 바꿔면 됨
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
            // HookCustomMessages(_router); // YOLO 전용
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

            // 1) 하트비트 감시
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
        /// 백그라운드에서 기체의 심장 박동(Heartbeat)을 지속적으로 감시하여, 지정된 시간(3초) 응답이 없으면 통신 단절로 처리합니다.
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
        public async Task Takeoff(float altitude)
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
                OnLogReceived?.Invoke($"[명령] 이륙 시도 (목표 고도: {altitude}m)...");

                await SetFlightMode(MavModeFlag.MavModeFlagCustomModeEnabled, 4);

                // 드론이 모드를 바꿀 수 있게 잠깐 숨 돌릴 시간(0.2초)을 줍니다.
                await Task.Delay(200);

                // 실제 이륙 명령 전송 (빈칸은 반드시 NaN으로 채움!)
                // param7 = altitude, 나머지 1~6은 NaN(미사용) 처리
                await cmd.SendCommandLong(
                    MavCmd.MavCmdNavTakeoff,
                    float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN,
                    altitude,
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

                    // 2. 비행 모드 변경: 컴퓨터 명령을 듣는 GUIDED(4) 모드로 전환
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
                            await GoTo(nextPoint.Lat, nextPoint.Lng);
                            currentDestination = nextPoint;

                            OnLogReceived?.Invoke($"[미션] 다음 웨이포인트로 이동 중... (위도: {Math.Round(nextPoint.Lat, 5)}, 경도: {Math.Round(nextPoint.Lng, 5)})");
                        }

                        // 4. 도착 판정: 드론이 해당 지점의 일정 반경(예: 3m) 안에 들어왔는지 확인
                        if (IsReached(nextPoint))
                        {
                            _waypoints.RemoveAt(0); // 도달한 지점은 리스트에서 제거
                            OnWaypointCountChanged?.Invoke(_waypoints.Count);
                            OnLogReceived?.Invoke($"[미션] 웨이포인트 도달 완료. (남은 지점: {_waypoints.Count}개)");

                            // 목적지가 삭제되었으므로 다음 루프에서 새로운 0번 점을 목적지로 설정하게 됨
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
        public async Task GoTo(double lat, double lng, float alt = 5.0f)
        {
            // 1. 안전장치: 드론 객체와 라우터 상태 확인
            if (_drone == null || !CurrentState.IsConnected || _router == null)
            {
                OnLogReceived?.Invoke("[경고] 기체가 연결되지 않아 이동 명령(GoTo)을 수행할 수 없습니다.");
                return;
            }

            try
            {
                // 2. 패킷 생성
                var packet = new SetPositionTargetGlobalIntPacket();

                // 3. 발신자 정보 설정
                packet.SystemId = (byte)GcsSystemId;
                packet.ComponentId = (byte)GcsComponentId;

                // 4. 내용물 설정
                // MAVLink 규격에 따라 위도/경도에 10의 7승을 곱해 정수로 변환합니다.
                packet.Payload.LatInt = (int)(lat * 1E7);
                packet.Payload.LonInt = (int)(lng * 1E7);
                packet.Payload.Alt = alt;

                // 이륙 지점 기준 상대 고도 좌표계 사용
                packet.Payload.CoordinateFrame = MavFrame.MavFrameGlobalRelativeAltInt;

                // 위치 정보만 사용하고 속도/가속도/요(Yaw)는 무시하는 비트 마스크(3576)
                packet.Payload.TypeMask = (PositionTargetTypemask)3576;

                // ✅ 하드코딩된 '1' 대신 실제 연결된 드론의 번호를 사용합니다.
                packet.Payload.TargetSystem = 1;
                packet.Payload.TargetComponent = 1; // 일반적으로 비행제어기는 1번 컴포넌트입니다.

                // 5. 전송
                await _router.Send(packet, _cts?.Token ?? CancellationToken.None);

                // 너무 잦은 로그 방지를 위해 StartMission에서 로그를 찍으므로 여기서는 생략하거나 디버그용으로만 사용합니다.
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] GoTo 명령 전송 중 예외 발생: {ex.Message}");
            }
        }

        /// <summary>
        /// 드론이 현재 목표 지점(Target)의 허용 범위 내에 도달했는지 판정합니다.
        /// </summary>
        /// <summary>
        /// 드론이 지정된 웨이포인트(지점)의 허용 범위(3m) 내에 도달했는지 확인합니다.
        /// </summary>
        private bool IsReached(GeoPoint destination)
        {
            // 한국 위도 기준 거리 계산 (근사치)
            double latDiff = (CurrentState.Latitude - destination.Lat) * 111000;
            double lngDiff = (CurrentState.Longitude - destination.Lng) * 88800;

            // 피타고라스 정리를 이용한 실제 거리(m) 계산
            double distanceInMeters = Math.Sqrt(Math.Pow(latDiff, 2) + Math.Pow(lngDiff, 2));

            // 오차 범위 3m 이내면 도착으로 판정
            bool reached = distanceInMeters < 3.0;

            return reached;
        }

        /// <summary>
        /// 진행 중인 자율 비행 임무를 즉시 중단하고 기체를 제자리에 정지시킵니다.
        /// </summary>
        public void StopMission()
        {
            if (!_isMissionRunning) return;

            _isMissionRunning = false;

            // 3. 소프트웨어만 멈추는 게 아니라, 드론에게도 '정지 명령'을 쏩니다.
            // 보통 GUIDED 모드에서 GoTo를 멈추려면 현재 위치에서 멈추도록 유도하거나,
            // 정지(LOITER/HOLD) 모드로 비행 모드를 강제 변경하는 것이 가장 안전합니다.
            OnLogReceived?.Invoke("[미션] 사용자에 의해 임무가 중단되었습니다. 기체를 정지합니다.");

            // 필요 시: await SetFlightMode(MavMode.MavModeFlagCustomModeEnabled, 4); 
            // (이미 GUIDED라면 현재 위치로 다시 GoTo를 쏴서 멈추게 할 수 있습니다.)
        }

        /// <summary>
        /// 지도에 새로운 웨이포인트(경유지) GPS 좌표를 추가합니다.
        /// </summary>
        /// <param name="lat">위도 (-90 ~ 90)</param>
        /// <param name="lng">경도 (-180 ~ 180)</param>
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
    }
}