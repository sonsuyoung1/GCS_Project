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
        private readonly CompositeDisposable _subs = new();
        private const string GcsRouterId = "GCS_ROUTER";
        private const int GcsSystemId = 255;
        private const int GcsComponentId = 190;
        private bool _telemetryHooked;
        private DateTime _lastHeartbeatUtc = DateTime.MinValue;
        private Task? _linkWatchdogTask;
        private IDisposable? _uiUpdateTimer;

        private IProtocolFactory? _protocol;
        private IProtocolRouter? _router;
        private IDeviceExplorer? _explorer;
        private IClientDevice? _drone;
        private readonly object _waypointsLock = new();
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
        public async Task StartAsync(string listenHost = "0.0.0.0", int listenPort = 14550, string droneIp = "192.168.0.72")
        {
            if (_router != null) return;

            _cts = new CancellationTokenSource();

            try
            {
                OnLogReceived?.Invoke("[시스템] MAVLink 서비스 시작 중...");

                _protocol = Protocol.Create(builder =>
                {
                    builder.RegisterMavlinkV2Protocol();
                    builder.Features.RegisterBroadcastFeature<MavlinkMessage>();
                    builder.Formatters.RegisterSimpleFormatter();
                });

                _router = _protocol.CreateRouter(GcsRouterId);

                // PX4 -> GCS (Telemetry 수신)
                _router.AddUdpPort(p =>
                {
                    p.Scheme = "udp";
                    p.Host = listenHost;
                    p.Port = listenPort;
                    p.IsEnabled = true;
                    p.Name = "udp_in";
                });

                // GCS -> PX4 (명령 / Heartbeat 송신)
                _router.AddUdpPort(p =>
                {
                    p.Scheme = "udp";
                    p.Host = droneIp;
                    p.Port = 14551;
                    p.IsEnabled = true;
                    p.Name = "udp_out";
                });

                var seq = new PacketSequenceCalculator();
                var identity = new MavlinkIdentity(GcsSystemId, GcsComponentId);

                _explorer = DeviceExplorer.Create(_router, b =>
                {
                    b.SetConfig(new ClientDeviceBrowserConfig
                    {
                        DeviceTimeoutMs = 1000,
                        DeviceCheckIntervalMs = 30_000
                    });
                    b.Factories.RegisterDefaultDevices(identity, seq, new InMemoryConfiguration());
                });

                OnLogReceived?.Invoke("[MAV] 장치 탐색 시작");

                // 1) 드론 발견 대기
                var found = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                _subs.Add(_explorer.Devices.ObserveAdd().Take(1).Subscribe(kvp =>
                {
                    _drone = kvp.Value.Value;
                    found.TrySetResult(true);
                }));

                var foundTask = await Task.WhenAny(found.Task, Task.Delay(5000, _cts.Token));
                if (foundTask != found.Task)
                {
                    throw new TimeoutException("드론 장치 탐색 시간 초과");
                }

                // 2) 드론 준비 완료 대기
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                _subs.Add(_drone!.State.Subscribe(st =>
                {
                    if (st == ClientDeviceState.Complete)
                    {
                        ready.TrySetResult(true);
                    }
                }));

                var readyTask = await Task.WhenAny(ready.Task, Task.Delay(5000, _cts.Token));
                if (readyTask != ready.Task)
                {
                    throw new TimeoutException("드론 장치 준비 시간 초과");
                }

                HookTelemetry(_drone);
                HookCommandAck(_drone);
                StartLinkWatchdog();

                OnLogReceived?.Invoke("[시스템] 드론 연결 완료");
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] MAVLink 서비스 시작 실패: {ex.Message}");
                await ResetCoreAsync();
                throw;
            }
        }

        /// <summary>
        /// 활성화된 모든 MAVLink 통신 연결과 비동기 작업을 안전하게 종료하고, 메모리 누수 방지를 위해 리소스를 해제합니다.
        /// </summary>
        public async Task StopAsync()
        {
            _isMissionRunning = false;

            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }

            _uiUpdateTimer?.Dispose();
            _uiUpdateTimer = null;

            _subs.Dispose();

            if (_explorer is IAsyncDisposable ax1)
                await ax1.DisposeAsync();
            _explorer = null;

            if (_router is IAsyncDisposable ax2)
                await ax2.DisposeAsync();
            _router = null;

            _protocol = null;
            _drone = null;

            _cts?.Dispose();
            _cts = null;

            CurrentState.IsConnected = false;
            OnDataUpdated?.Invoke(CurrentState.Clone());
            OnLogReceived?.Invoke("[MAV] 연결 종료됨");
        }

        private async Task ResetCoreAsync()
        {
            try
            {
                _uiUpdateTimer?.Dispose();
                _uiUpdateTimer = null;

                _subs.Dispose();

                if (_explorer is IAsyncDisposable ax1)
                    await ax1.DisposeAsync();
                _explorer = null;

                if (_router is IAsyncDisposable ax2)
                    await ax2.DisposeAsync();
                _router = null;

                _protocol = null;
                _drone = null;

                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;

                _telemetryHooked = false;
                _lastHeartbeatUtc = DateTime.MinValue;
                _linkWatchdogTask = null;

                CurrentState.IsConnected = false;
            }
            catch
            {
                // 시작 실패 정리 중 예외는 무시
            }
        }
        #endregion

        #region [5. 자원 해제 (Dispose)]
        public async ValueTask DisposeAsync() => await StopAsync();
        #endregion
    }
}