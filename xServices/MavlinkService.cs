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
using System.Threading;
using System.Threading.Tasks;

namespace GCS_Project.xServices
{
    public class MavlinkService : IAsyncDisposable
    {
        private readonly List<GeoPoint> _waypoints = new();
        private bool _isMissionRunning;

        private CancellationTokenSource? _cts;

        // v4 core
        private IProtocolFactory? _protocol;
        private IProtocolRouter? _router;
        private IDeviceExplorer? _explorer;
        private IClientDevice? _drone;

        private readonly CompositeDisposable _subs = new();

        public DroneState CurrentState { get; } = new DroneState();
        public ObservableCollection<DetectedTarget> DetectedTargets { get; } = new ObservableCollection<DetectedTarget>();

        public event Action<DroneState>? OnDataUpdated;
        public event Action<string>? OnLogReceived;

        public async Task StartAsync(string listenHost = "0.0.0.0", int listenPort = 14550)
        {
            OnLogReceived?.Invoke("[MAV] StartAsync entered");
            if (_router != null) return;

            OnLogReceived?.Invoke("[MAV] StartAsync called");

            _cts = new CancellationTokenSource();

            _protocol = Protocol.Create(builder =>
            {
                builder.RegisterMavlinkV2Protocol();
                builder.Features.RegisterBroadcastFeature<MavlinkMessage>();
                builder.Formatters.RegisterSimpleFormatter();
            });
            OnLogReceived?.Invoke("[MAV] Protocol created");

            _router = _protocol.CreateRouter("GCS_ROUTER");
            OnLogReceived?.Invoke("[MAV] Router created");

            AddUdpListenPort(_router, listenHost, listenPort);
            OnLogReceived?.Invoke("[MAV] UDP port added");
            OnLogReceived?.Invoke($"[MAV] UDP listen {listenHost}:{listenPort}");

            var seq = new PacketSequenceCalculator();
            var identity = new MavlinkIdentity(255, 255);

            _explorer = DeviceExplorer.Create(_router, b =>
            {
                b.SetConfig(new ClientDeviceBrowserConfig
                {
                    DeviceTimeoutMs = 1000,
                    DeviceCheckIntervalMs = 30_000
                });

                b.Factories.RegisterDefaultDevices(
                    new MavlinkIdentity(identity.SystemId, identity.ComponentId),
                    seq,
                    new InMemoryConfiguration()
                );
            });
            OnLogReceived?.Invoke("[MAV] DeviceExplorer created");

            var found = new TaskCompletionSource();
            _subs.Add(_explorer.Devices.ObserveAdd().Take(1).Subscribe(kvp =>
            {
                _drone = kvp.Value.Value;
                OnLogReceived?.Invoke("[MAV] Device found");
                found.TrySetResult();
            }));
            OnLogReceived?.Invoke("[MAV] Waiting device(found)...");
            await found.Task;

            var ready = new TaskCompletionSource();
            _subs.Add(_drone!.State.Subscribe(st =>
            {
                OnLogReceived?.Invoke($"[MAV] Drone state: {st}");
                if (st == ClientDeviceState.Complete) ready.TrySetResult();
            }));
            OnLogReceived?.Invoke("[MAV] Waiting device(ready)...");
            await ready.Task;

            HookTelemetry(_drone);
            OnLogReceived?.Invoke("[MAV] Telemetry hooked");

            CurrentState.IsConnected = true;
            OnDataUpdated?.Invoke(CurrentState);
            OnLogReceived?.Invoke("[MAV] Connected");
        }

        // 여기만 형님 환경에 맞게 “메서드 이름”을 정확히 골라 꽂으면 됨
        private static void AddUdpListenPort(IProtocolRouter router, string host, int port)
        {
            router.AddUdpPort(p =>
            {
                p.Scheme = "udp";      // 있으면 넣는 게 확실
                p.Host = host;         // "0.0.0.0"
                p.Port = port;         // 14550
                p.IsEnabled = true;    // ★ 이거 중요

                // 있으면 디버깅에 도움
                p.Name = "udp_in";


            });
        }

        private void HookTelemetry(IClientDevice drone)
        {
            // Heartbeat 예시 (형님 패키지에 IHeartbeatClient가 있으면 이게 제일 깔끔)
            var hb = drone.GetMicroservice<IHeartbeatClient>();
            if (hb != null)
            {
                _subs.Add(hb.RawHeartbeat.Subscribe(_ =>
                {
                    CurrentState.IsConnected = true;
                    OnDataUpdated?.Invoke(CurrentState);
                }));
            }

            // TODO: GlobalPositionInt / VfrHud / SysStatus는
            // 형님 프로젝트에서 GetMicroservice<...> 자동완성으로 실제 타입 이름 확인해서 붙이면 됨
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
            OnLogReceived?.Invoke("[MAV] Disconnected");
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }

    }
}