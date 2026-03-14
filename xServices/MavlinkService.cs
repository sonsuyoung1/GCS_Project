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
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.Metrics;
using System.Net.Sockets;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace GCS_Project.xServices
{
    public partial class MavlinkService : IAsyncDisposable
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
        public async Task StartAsync(string listenHost = "0.0.0.0", int listenPort = 14550, string droneIp = "192.168.0.72") // ip는 와이파이 달라질 때마다 바꿔면 됨
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


        #region [5. 자원 해제 (Dispose)]
        public async ValueTask DisposeAsync() => await StopAsync();
        #endregion
    }
}