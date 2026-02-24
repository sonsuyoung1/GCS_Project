using Asv.Cfg;
using Asv.Common;
using Asv.IO;
using Asv.Mavlink;
using Asv.Mavlink.Common;
using GCS_Project.Models;
using ObservableCollections;
using R3;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

// GeoPoint 이름 충돌 방지
using GeoPoint = GCS_Project.Models.GeoPoint;

namespace GCS_Project.xServices
{
    public class MavlinkService : IAsyncDisposable
    {
        #region [1. 필드 및 설정 (Core Fields)]

        private const string GcsRouterId = "GCS_ROUTER";
        private const int GcsSystemId = 255;
        private const int GcsComponentId = 255;

        // v4 Core 객체
        private IProtocolFactory? _protocol;
        private IProtocolRouter? _router;
        private IDeviceExplorer? _explorer;
        private IClientDevice? _drone;

        private readonly List<GeoPoint> _waypoints = new();
        private bool _isMissionRunning;
        private CancellationTokenSource? _cts;
        private readonly CompositeDisposable _subs = new();

        #endregion

        #region [2. 데이터 속성 및 상태 (State & Data)]
        public DroneState CurrentState { get; } = new DroneState();
        public ObservableCollection<DetectedTarget> DetectedTargets { get; } = new();
        #endregion

        #region [3. 이벤트 (Events)]
        public event Action<DroneState>? OnDataUpdated;
        public event Action<string>? OnLogReceived;
        public event Action<int>? OnWaypointCountChanged;
        public event Action? OnMissionCompleted;
        // public event Action<int, double, double, string, bool>? OnTargetLocationUpdated;
        #endregion

        #region [4. 초기화 및 서비스 제어 (Lifecycle)]
        public MavlinkService()
        {
            // 초기 위치 설정 (한서대학교)
            CurrentState.Latitude = 36.6906;
            CurrentState.Longitude = 126.5820;
            CurrentState.IsConnected = false;
        }

        public async Task StartAsync(string listenHost = "0.0.0.0", int listenPort = 14550, string droneIp = "192.168.1.136")
        {
            if (_router != null) return;
            OnLogReceived?.Invoke("[MAV] MAVLink 서비스 시작 중...");

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
            _router = _protocol.CreateRouter(GcsRouterId);
            _router.AddUdpPort(p =>
            {
                p.Scheme = "udp";
                p.Host = listenHost;
                p.Port = listenPort;
                p.IsEnabled = true;
                p.Name = "udp_in";
            });

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
            // HookCustomMessages(_router); // YOLO 전용

            CurrentState.IsConnected = true;
            OnDataUpdated?.Invoke(CurrentState);
            OnLogReceived?.Invoke("[시스템] 연결 및 초기화 완료");
        }

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
            OnLogReceived?.Invoke("[시스템] 연결 종료됨");
        }
        #endregion


        #region [5. 텔레메트리 핸들링 (Telemetry Hooks)]
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
                    MavResult.MavResultAccepted => " 수락 및 실행됨",
                    MavResult.MavResultTemporarilyRejected => " 일시적 거절 (드론 바쁨)",
                    MavResult.MavResultDenied => " 거절됨 (조건 불만족)",
                    MavResult.MavResultUnsupported => " 지원하지 않는 명령",
                    MavResult.MavResultFailed => " 실행 실패",
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


        #region [6. 비행 명령 제어 (Flight Commands)]
        /// <summary>
        /// 지정된 고도로 이륙 명령(Takeoff)을 전송합니다.
        /// </summary>
        /// <param name="altitude">목표 이륙 고도 (미터)</param>
        public async Task Takeoff(float altitude)
        {
            // 1. 안전장치: 드론 객체와 명령 부서가 정상인지 확인
            if (_drone == null)
            {
                OnLogReceived?.Invoke(" [경고] 기체가 연결되지 않아 이륙할 수 없습니다.");
                return;
            }

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            // 2. 명령 전송 및 에러 방어막 (try-catch)
            try
            {
                OnLogReceived?.Invoke($" [명령] 이륙 시도 (목표 고도: {altitude}m)...");

                // Param 7 자리에 목표 고도(altitude)를 넣어서 전송!
                await cmd.CommandLong(MavCmd.MavCmdNavTakeoff, 0, 0, 0, 0, 0, 0, altitude);
            }
            catch (Exception ex)
            {
                // 통신 지연이나 에러 발생 시 프로그램이 안 튕기도록 조용히 로그만 출력
                OnLogReceived?.Invoke($" [오류] 이륙 명령 전송 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 현재 위치에서 즉시 착륙 명령(Land)을 전송합니다.
        /// </summary>
        public async Task Landing()
        {
            // 1. 안전장치: 널 포인터 폭발(Crash)을 막기 위해 꼼꼼하게 체크
            if (_drone == null)
            {
                OnLogReceived?.Invoke("[경고] 기체가 연결되지 않아 착륙 명령을 내릴 수 없습니다.");
                return;
            }

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            // 2. 명령 전송 및 에러 방어막
            try
            {
                OnLogReceived?.Invoke("[명령] 착륙 시도...");

                // 착륙은 특별한 파라미터 없이 전부 0으로 채워서 보냄
                await cmd.CommandLong(MavCmd.MavCmdNavLand, 0, 0, 0, 0, 0, 0, 0);
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
            // 1. 안전장치: 조종사가 버튼을 눌렀는데 드론이 없으면 '침묵'하지 않고 이유를 알려줌!
            if (_drone == null)
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

                // 20번 = MavCmdNavReturnToLaunch (RTL), 파라미터는 모두 0
                await cmd.CommandLong(MavCmd.MavCmdNavReturnToLaunch, 0, 0, 0, 0, 0, 0, 0);
            }
            catch (Exception ex)
            {
                // 전파가 끊기거나 명령이 씹혀도 프로그램이 죽지 않도록 방어!
                OnLogReceived?.Invoke($"[오류] 복귀(RTL) 명령 전송 실패: {ex.Message}");
            }
        }

        public async Task SetFlightMode(MavMode mode, uint customMode)
        {
            // 1. 추상 클래스 대신 '완성된 패킷 클래스'를 사용합니다!
            var packet = new SetModePacket();

            // 2. 보내는 사람(GCS)의 정보를 봉투 겉면에 적습니다.
            packet.SystemId = (byte)GcsSystemId;
            packet.ComponentId = (byte)GcsComponentId;

            // 3. 내용물(Payload)을 채웁니다.
            packet.Payload.BaseMode = mode;
            packet.Payload.CustomMode = customMode;
            packet.Payload.TargetSystem = 1;

            // 4. 완벽하게 2개의 인수(메시지, 토큰)만 사용해서 보냅니다.
            await _router!.Send(packet, _cts!.Token);
        }

        /*// Yolo로 객체 추적 로직
        public async Task GoTo(double lat, double lng, float alt = 5.0f)
        {
            // 1. 완성된 패킷 클래스 생성
            var packet = new SetPositionTargetGlobalIntPacket();

            // 2. 헤더 정보(Identity) 설정
            packet.SystemId = (byte)GcsSystemId;
            packet.ComponentId = (byte)GcsComponentId;

            // 3. 페이로드 설정
            packet.Payload.LatInt = (int)(lat * 1E7);
            packet.Payload.LonInt = (int)(lng * 1E7);
            packet.Payload.Alt = alt;
            packet.Payload.CoordinateFrame = MavFrame.MavFrameGlobalRelativeAltInt;
            packet.Payload.TypeMask = (PositionTargetTypemask)3576;
            packet.Payload.TargetSystem = 1;
            packet.Payload.TargetComponent = 1;

            // 4. 전송 (인수 2개로 완벽하게 일치!)
            await _router!.Send(packet, _cts!.Token);
        }*/
        #endregion

        #region [7. 미션 로직 (Mission & Waypoints)]
        // 웨이포인트 추가
        public void AddWaypoint(double lat, double lng)
        {
            _waypoints.Add(new GeoPoint { Lat = lat, Lng = lng });
            OnWaypointCountChanged?.Invoke(_waypoints.Count);
        }

        // 마지막 웨이포인트 삭제
        public void UndoWaypoint()
        {
            if (_waypoints.Count > 0)
            {
                // 리스트의 맨 마지막 요소(가장 최근에 찍은 점)를 제거합니다.
                _waypoints.RemoveAt(_waypoints.Count - 1);

                // UI 화면에 웨이포인트 개수가 줄어들었다고 알려줍니다.
                OnWaypointCountChanged?.Invoke(_waypoints.Count);
                OnLogReceived?.Invoke($"[미션] 마지막 웨이포인트를 취소했습니다. (남은 개수: {_waypoints.Count})");
            }
            else
            {
                OnLogReceived?.Invoke("[경고] 취소할 웨이포인트가 없습니다.");
            }
        }

        // 모든 웨이포인트 싹 다 지우기
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
        }

        // 미션 시작 메서드.
        public void StartMission()
        {
            if (_isMissionRunning || _waypoints.Count == 0) return;
            _isMissionRunning = true;

            Task.Run(async () => {
                OnLogReceived?.Invoke("[미션] 웨이포인트 임무 시작");
                
                await SetFlightMode((MavMode)1, 4);

                while (_isMissionRunning && _waypoints.Count > 0)
                {
                    var target = _waypoints[0];
                    await GoTo(target.Lat, target.Lng);

                    if (IsReached(target))
                    {
                        _waypoints.RemoveAt(0);
                        OnWaypointCountChanged?.Invoke(_waypoints.Count);
                        OnLogReceived?.Invoke($"[미션] 지점 도달. 남은 지점: {_waypoints.Count}");
                    }
                    await Task.Delay(1000);
                }
                _isMissionRunning = false;
                OnMissionCompleted?.Invoke();
                OnLogReceived?.Invoke("[미션] 모든 임무 완료");
            });
        }

        private bool IsReached(GeoPoint target)
        {
            double dist = Math.Sqrt(Math.Pow(CurrentState.Latitude - target.Lat, 2) + Math.Pow(CurrentState.Longitude - target.Lng, 2));
            return dist < 0.0001; // 약 10m 내외
        }

        

        /*public async Task StopTracking()
        {
            // 아두파일럿 기준 CustomMode 5번이 'LOITER(가만히 떠 있기)' 모드입니다.
            await SetFlightMode((MavMode)1, 5);
            OnLogReceived?.Invoke("[명령] 추적 중지 및 제자리 정지(LOITER) 모드 전환");
        }*/

        public void StopMission() => _isMissionRunning = false;
        #endregion

        #region [8. 자원 해제 (Dispose)]
        public async ValueTask DisposeAsync() => await StopAsync();
        #endregion
    }
}