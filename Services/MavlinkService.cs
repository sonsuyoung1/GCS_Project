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
using System.Threading;
using System.Threading.Tasks;

namespace GCS_Project.Services
{
    public partial class MavlinkService : IAsyncDisposable
    {
        #region [1. 상수]

        private const string GcsRouterId = "GCS_ROUTER";
        private const int GcsSystemId = 255;
        private const int GcsComponentId = 190;

        #endregion


        #region [2. MAVLink 핵심 필드]

        private IProtocolFactory? _protocol;
        private IProtocolRouter? _router;
        private IDeviceExplorer? _explorer;
        private IClientDevice? _drone;

        private CancellationTokenSource? _cts;
        private readonly CompositeDisposable _subs = new();

        private bool _telemetryHooked;
        private DateTime _lastHeartbeatUtc = DateTime.MinValue;
        private Task? _linkWatchdogTask;
        private IDisposable? _uiUpdateTimer;

        #endregion


        #region [3. 미션 및 상태 필드]

        private readonly List<GeoPoint> _waypoints = new();
        private readonly object _waypointsLock = new();

        private bool _isMissionRunning;
        private string _lastParsedMode = "IDLE";

        #endregion


        #region [4. 텔레메트리 원본 데이터]

        private readonly object _telemetryRawLock = new();

        private long _rawGpSeq = 0;

        private double _rawLat;
        private double _rawLon;
        private double _rawRelAlt;
        private double _rawLocalAlt;

        private double _rawRoll;
        private double _rawPitch;
        private double _rawYaw;

        private double _rawSpeed;
        private int _rawBattery;

        private bool _rawIsConnected;
        private bool _rawIsArmed;

        private DateTime _rawGpUtc = DateTime.MinValue;
        private DateTime _rawLpnUtc = DateTime.MinValue;
        private DateTime _rawAttUtc = DateTime.MinValue;
        private DateTime _rawVfrUtc = DateTime.MinValue;

        #endregion


        #region [5. 상태 객체 및 이벤트]

        private readonly object _stateLock = new();

        public DroneState CurrentState { get; } = new DroneState();

        public event Action<DroneState>? OnDataUpdated;
        public event Action<string>? OnLogReceived;
        public event Action<int>? OnWaypointCountChanged;
        public event Action? OnMissionCompleted;
        public event Action<string>? OnFlightModeChanged;

        #endregion


        #region [6. 서비스 시작]

        // MAVLink 라우터를 생성하고 PX4 기체 연결을 시작한다.
        public async Task StartAsync(string listenHost, int listenPort, string droneIp, int dronePort)
        {
            if (_router != null) return;

            _cts = new CancellationTokenSource();

            try
            {
                OnLogReceived?.Invoke("[시스템] MAVLink 서비스 시작 중...");

                CreateProtocolAndRouter();
                AddUdpPorts(listenHost, listenPort, droneIp, dronePort);
                CreateDeviceExplorer();

                OnLogReceived?.Invoke("[MAV] 장치 탐색 시작");

                await WaitForDroneFound();
                await WaitForDroneReady();

                HookTelemetry(_drone!);
                HookCommandAck(_drone!);
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

        // MAVLink 프로토콜과 라우터를 생성한다.
        private void CreateProtocolAndRouter()
        {
            _protocol = Protocol.Create(builder =>
            {
                builder.RegisterMavlinkV2Protocol();
                builder.Features.RegisterBroadcastFeature<MavlinkMessage>();
                builder.Formatters.RegisterSimpleFormatter();
            });

            _router = _protocol.CreateRouter(GcsRouterId);
        }

        // PX4 수신 포트와 명령 송신 포트를 라우터에 추가한다.
        private void AddUdpPorts(
            string listenHost,
            int listenPort,
            string droneIp,
            int dronePort)
        {
            if (_router == null)
                throw new InvalidOperationException("MAVLink 라우터가 생성되지 않았습니다.");

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
                p.Host = droneIp;
                p.Port = dronePort;
                p.IsEnabled = true;
                p.Name = "udp_out";
            });
        }

        // 라우터를 기반으로 MAVLink 장치 탐색기를 생성한다.
        private void CreateDeviceExplorer()
        {
            if (_router == null)
                throw new InvalidOperationException("MAVLink 라우터가 생성되지 않았습니다.");

            var sequence = new PacketSequenceCalculator();
            var identity = new MavlinkIdentity(GcsSystemId, GcsComponentId);

            _explorer = DeviceExplorer.Create(_router, builder =>
            {
                builder.SetConfig(new ClientDeviceBrowserConfig
                {
                    DeviceTimeoutMs = 1000,
                    DeviceCheckIntervalMs = 30_000
                });

                builder.Factories.RegisterDefaultDevices(
                    identity,
                    sequence,
                    new InMemoryConfiguration()
                );
            });
        }

        // MAVLink 장치 목록에서 첫 번째 드론이 발견될 때까지 기다린다.
        private async Task WaitForDroneFound()
        {
            if (_explorer == null)
                throw new InvalidOperationException("MAVLink 장치 탐색기가 생성되지 않았습니다.");

            var found = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );

            _subs.Add(_explorer.Devices.ObserveAdd().Take(1).Subscribe(device =>
            {
                _drone = device.Value.Value;
                found.TrySetResult(true);
            }));

            await found.Task;
        }

        // 발견된 드론이 통신 가능한 Complete 상태가 될 때까지 기다린다.
        private async Task WaitForDroneReady()
        {
            if (_drone == null)
                throw new InvalidOperationException("PX4 드론 장치를 찾지 못했습니다.");

            var ready = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );

            _subs.Add(_drone.State.Subscribe(state =>
            {
                if (state == ClientDeviceState.Complete)
                {
                    ready.TrySetResult(true);
                }
            }));

            await ready.Task;
        }

        #endregion


        #region [7. 상태 발행]

        // 원본 텔레메트리 값을 CurrentState에 반영하고 ViewModel에 전달한다.
        private void PublishState()
        {
            var raw = CaptureRawTelemetrySnapshot();

            bool shouldNotifyConnected = ApplySnapshotToCurrentState(raw);

            if (shouldNotifyConnected)
            {
                OnLogReceived?.Invoke("[MAV] 통신 연결됨");
            }

            DroneState snapshot;

            lock (_stateLock)
            {
                snapshot = CurrentState.Clone();
            }

            OnDataUpdated?.Invoke(snapshot);
        }

        // 텔레메트리 원본 값을 한 번에 복사한다.
        private RawTelemetrySnapshot CaptureRawTelemetrySnapshot()
        {
            lock (_telemetryRawLock)
            {
                return new RawTelemetrySnapshot(
                    _rawLat,
                    _rawLon,
                    _rawRelAlt,
                    _rawLocalAlt,
                    _rawRoll,
                    _rawPitch,
                    _rawYaw,
                    _rawSpeed,
                    _rawBattery,
                    _rawIsConnected,
                    _rawIsArmed
                );
            }
        }

        // 텔레메트리 스냅샷을 UI용 CurrentState에 반영한다.
        private bool ApplySnapshotToCurrentState(RawTelemetrySnapshot raw)
        {
            lock (_stateLock)
            {
                bool wasDisconnected = !CurrentState.IsConnected;
                bool isNowConnected = raw.IsConnected;

                CurrentState.Latitude = raw.Latitude;
                CurrentState.Longitude = raw.Longitude;
                CurrentState.Altitude = raw.RelativeAltitude;
                CurrentState.LocalAltitude = raw.LocalAltitude;

                CurrentState.Roll = raw.Roll;
                CurrentState.Pitch = raw.Pitch;
                CurrentState.Yaw = raw.Yaw;

                CurrentState.Speed = raw.Speed;
                CurrentState.BatteryPercent = raw.BatteryPercent;

                CurrentState.IsConnected = raw.IsConnected;
                CurrentState.IsArmed = raw.IsArmed;

                return wasDisconnected && isNowConnected;
            }
        }

        private sealed record RawTelemetrySnapshot(
            double Latitude,
            double Longitude,
            double RelativeAltitude,
            double LocalAltitude,
            double Roll,
            double Pitch,
            double Yaw,
            double Speed,
            int BatteryPercent,
            bool IsConnected,
            bool IsArmed
        );

        #endregion


        #region [8. 서비스 종료 및 초기화]

        // MAVLink 통신과 관련된 리소스를 안전하게 종료한다.
        public async Task StopAsync()
        {
            _isMissionRunning = false;

            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }

            await DisposeCoreAsync();

            lock (_telemetryRawLock)
            {
                _rawIsConnected = false;
                _rawIsArmed = false;
            }

            PublishState();
            OnLogReceived?.Invoke("[MAV] 연결 종료됨");
        }

        // 시작 실패 또는 재연결 준비를 위해 내부 상태를 초기화한다.
        private async Task ResetCoreAsync()
        {
            try
            {
                await DisposeCoreAsync();

                _telemetryHooked = false;
                _lastHeartbeatUtc = DateTime.MinValue;
                _linkWatchdogTask = null;

                ResetRawTelemetry();
            }
            catch
            {
                // 시작 실패 정리 중 발생한 예외는 무시한다.
            }
        }

        // 라우터, 탐색기, 타이머, 취소 토큰을 정리한다.
        private async Task DisposeCoreAsync()
        {
            _uiUpdateTimer?.Dispose();
            _uiUpdateTimer = null;

            _subs.Dispose();

            if (_explorer is IAsyncDisposable explorer)
            {
                await explorer.DisposeAsync();
            }

            _explorer = null;

            if (_router is IAsyncDisposable router)
            {
                await router.DisposeAsync();
            }

            _router = null;
            _protocol = null;
            _drone = null;

            _cts?.Dispose();
            _cts = null;
        }

        // 원본 텔레메트리 값을 초기 상태로 되돌린다.
        private void ResetRawTelemetry()
        {
            lock (_telemetryRawLock)
            {
                _rawLat = 0;
                _rawLon = 0;
                _rawRelAlt = 0;
                _rawLocalAlt = 0;

                _rawRoll = 0;
                _rawPitch = 0;
                _rawYaw = 0;

                _rawSpeed = 0;
                _rawBattery = 0;

                _rawIsConnected = false;
                _rawIsArmed = false;

                _rawGpUtc = DateTime.MinValue;
                _rawLpnUtc = DateTime.MinValue;
                _rawAttUtc = DateTime.MinValue;
                _rawVfrUtc = DateTime.MinValue;

                _rawGpSeq = 0;
            }
        }

        #endregion


        #region [9. 자원 해제]

        // MavlinkService가 해제될 때 통신 리소스도 함께 정리한다.
        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }

        #endregion
    }
}