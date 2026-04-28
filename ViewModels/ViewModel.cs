using Asv.Mavlink;
using Asv.Mavlink.Minimal;
using GCS_Project.Data;
using GCS_Project.Models;
using GCS_Project.Services;
using System;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace GCS_Project.ViewModels
{
    // UI 명령, 드론 상태, 서비스 이벤트를 조율하는 중심 ViewModel
    public partial class ViewModel : ObservableObject
    {
        #region [1. 비행 모드 상수]
        private const string MODE_IDLE = "IDLE";
        private const string MODE_TAKEOFF = "TAKEOFF";
        private const string MODE_MISSION = "MISSION";
        private const string MODE_MANUAL = "MANUAL";
        private const string MODE_HOLD = "HOLD";
        #endregion


        #region [2. 서비스 및 상태 객체]
        private readonly MavlinkService _mavlinkService;
        private readonly YoloMetaUdpService _yoloMetaUdpService;

        private readonly DroneState _uiDrone = new DroneState();
        private readonly ObservableCollection<DetectedTarget> _detectedTargets = new();
        private readonly List<GeoPoint> _waypoints = new List<GeoPoint>();
        private readonly UbuntuCommandService _ubuntuCommandService;
        #endregion


        #region [3. 화면 표시 상태]
        private string _flightMode = MODE_IDLE;
        private string _chatLog = "드론 연결 대기 중...";
        private readonly StringBuilder _chatLogBuffer = new StringBuilder("드론 연결 대기 중...");

        private string _currentDateTime = "";

        private double _pitchOffset;
        private double _headingOffset;
        #endregion


        #region [4. 비행 및 미션 상태]
        private int _currentWaypointCount = 0;
        private bool _mavStarting;
        private bool _isFlyingHigh = false;
        private bool _isTakeoffMarkerDrawn = false;
        private bool _wasArmed = false;

        private int _currentFlightId;
        private readonly DateTime _programStartTime = DateTime.Now;
        #endregion


        #region [5. 타겟 및 타격 상태]
        private DetectedTarget? _selectedTarget;
        private bool _isStriking = false;
        private System.Timers.Timer? _targetLostTimer;
        private DateTime _lastTargetLogSaveTime = DateTime.MinValue;
        #endregion


        #region [6. 탐색 및 기록 상태]
        private CancellationTokenSource? _scanCts;
        private bool _isScanning = false;
        private bool _batteryWarningShown = false;

        private DateTime? _selectedHistoryDate = DateTime.Today;
        private bool _isHistoryVisible = false;
        #endregion


        #region [7. 버튼 상태 갱신용 필드]
        // 버튼 활성화에 영향을 주는 값만 묶어 불필요한 커맨드 갱신을 줄인다.
        private sealed record CommandStateSnapshot(
            string FlightMode,
            bool IsConnected,
            bool IsArmed,
            int WaypointCount,
            bool HasSelectedTarget,
            bool IsStriking
        );

        private CommandStateSnapshot? _lastCommandStateSnapshot;
        #endregion


        #region [8. 타이머]
        private System.Windows.Threading.DispatcherTimer? _clockTimer;
        #endregion


        #region [9. 기본 속성]
        public MavlinkService MavlinkService => _mavlinkService;
        public DroneState Drone => _uiDrone;
        public ObservableCollection<DetectedTarget> DetectedTargets => _detectedTargets;

        public string FlightMode
        {
            get => _flightMode;
            set
            {
                if (SetProperty(ref _flightMode, value))
                {
                    RefreshCommandStatesIfNeeded();
                }
            }
        }

        public string ChatLog
        {
            get => _chatLog;
            set => SetProperty(ref _chatLog, value);
        }

        public string CurrentDateTime
        {
            get => _currentDateTime;
            set => SetProperty(ref _currentDateTime, value);
        }

        public DetectedTarget? SelectedTarget
        {
            get => _selectedTarget;
            set
            {
                if (SetProperty(ref _selectedTarget, value))
                {
                    RefreshCommandStatesIfNeeded();

                    // 선택된 타겟 ID를 지도에 알림
                    TargetSelectionChanged?.Invoke(value?.ID);
                    if (value != null)
                    {
                        _yoloMetaUdpService.SendTargetId(value.ID);
                        AppendLog($"[GUIDANCE] 선택 타겟 ID 전송: {value.ID}");
                    }
                }
            }
        }

        public bool IsStriking
        {
            get => _isStriking;
            set
            {
                if (SetProperty(ref _isStriking, value))
                {
                    RefreshCommandStatesIfNeeded();
                }
            }
        }

        public double PitchOffset
        {
            get => _pitchOffset;
            set => SetProperty(ref _pitchOffset, value);
        }

        public double HeadingOffset
        {
            get => _headingOffset;
            set => SetProperty(ref _headingOffset, value);
        }

        public DateTime? SelectedHistoryDate
        {
            get => _selectedHistoryDate;
            set
            {
                if (SetProperty(ref _selectedHistoryDate, value))
                {
                    ResetHistoryPathWhenDateChanged();
                }
            }
        }
        #endregion


        #region [10. 버튼 활성화 조건]
        public bool IsArmEnabled =>
            FlightMode == MODE_IDLE &&
            Drone.IsConnected &&
            !Drone.IsArmed;

        public bool IsDisarmEnabled =>
            FlightMode == MODE_IDLE &&
            Drone.IsConnected &&
            Drone.IsArmed;

        public bool IsTakeoffEnabled =>
            FlightMode == MODE_IDLE &&
            Drone.IsConnected &&
            Drone.IsArmed;

        public bool IsMissionStartEnabled =>
            (FlightMode == MODE_TAKEOFF || FlightMode == MODE_HOLD) &&
            _currentWaypointCount > 0 &&
            Drone.IsConnected &&
            Drone.IsArmed;

        public bool IsLandingEnabled =>
            (FlightMode == MODE_TAKEOFF || FlightMode == MODE_HOLD) &&
            Drone.IsConnected &&
            Drone.IsArmed;

        public bool IsRtlEnabled =>
            (FlightMode == MODE_TAKEOFF || FlightMode == MODE_HOLD || FlightMode == MODE_MISSION) &&
            Drone.IsConnected &&
            Drone.IsArmed;

        public bool IsUndoEnabled =>
            (FlightMode == MODE_IDLE || FlightMode == MODE_TAKEOFF || FlightMode == MODE_HOLD) &&
            _currentWaypointCount > 0 &&
            FlightMode != MODE_MISSION;

        public bool IsClearEnabled =>
            (FlightMode == MODE_IDLE || FlightMode == MODE_TAKEOFF || FlightMode == MODE_HOLD) &&
            _currentWaypointCount > 0 &&
            FlightMode != MODE_MISSION;

        public bool IsStopEnabled =>
            FlightMode == MODE_MISSION &&
            Drone.IsConnected;

        public bool IsStrikeEnabled =>
            SelectedTarget != null &&
            (FlightMode == MODE_TAKEOFF || FlightMode == MODE_HOLD || FlightMode == MODE_MISSION) &&
            Drone.IsConnected &&
            Drone.IsArmed &&
            !IsStriking;

        public bool IsStopStrikeEnabled =>
            IsStriking &&
            Drone.IsConnected;
        #endregion


        #region [11. 커맨드]
        public ICommand StartMissionCommand { get; private set; } = null!;
        public ICommand StopMissionCommand { get; private set; } = null!;

        public ICommand ArmCommand { get; private set; } = null!;
        public ICommand DisarmCommand { get; private set; } = null!;
        public ICommand TakeoffCommand { get; private set; } = null!;
        public ICommand LandingCommand { get; private set; } = null!;
        public ICommand RtlCommand { get; private set; } = null!;

        public ICommand UndoWaypointCommand { get; private set; } = null!;
        public ICommand ClearWaypointsCommand { get; private set; } = null!;

        public ICommand IncreaseAltitudeCommand { get; private set; } = null!;
        public ICommand DecreaseAltitudeCommand { get; private set; } = null!;
        public ICommand IncreaseSpeedCommand { get; private set; } = null!;
        public ICommand DecreaseSpeedCommand { get; private set; } = null!;

        public ICommand ShowHistoryCommand { get; private set; } = null!;
        public ICommand ConnectPx4MavlinkCommand { get; private set; } = null!;

        public ICommand StrikeCommand { get; private set; } = null!;
        public ICommand StopStrikeCommand { get; private set; } = null!;

        #endregion


        #region [12. View 통신용 이벤트]
        public event Action? UndoWaypointRequested;
        public event Action? ClearWaypointsRequested;
        public event Action? AllClearRequested;

        public event Action<double, double, double>? PositionUpdated;
        public event Action<double, double>? TakeoffPointSet;
        public event Action<double, double>? PersonDetectedOnMap;

        public event Action<List<Tuple<double, double>>>? HistoryPathRequested;
        public event Action<int, double, double, string, bool>? TargetMarkerUpdated;
        public event Action<int?>? TargetSelectionChanged;
        public event Action<int>? TargetMarkerRemoved;
        #endregion


        #region [13. 생성자]
        public ViewModel()
        {
            _mavlinkService = new MavlinkService();
            _yoloMetaUdpService = new YoloMetaUdpService(
                listenPort: 5001,
                rosCommandIp: "192.168.0.61",
                rosCommandPort: 5006
            );

            _ubuntuCommandService = new UbuntuCommandService(
                host: "192.168.0.61",      // 팀원 Ubuntu IP
                username: "joseonggeon",
                password: "1234"
            );

            InitializeCommands();
            InitializeTargetLostTimer();
            InitializeClockTimer();

            InitializeService();
            InitializeFlightSession();
        }
        #endregion


        #region [14. 초기화 메서드]
        // 버튼과 UI 명령에 연결될 커맨드를 초기화한다.
        private void InitializeCommands()
        {
            StartMissionCommand = new RelayCommand(ExecuteStartMission, () => IsMissionStartEnabled);
            StopMissionCommand = new RelayCommand(ExecuteStopMission, () => IsStopEnabled);

            ArmCommand = new RelayCommand(ExecuteArm, () => IsArmEnabled);
            DisarmCommand = new RelayCommand(ExecuteDisarm, () => IsDisarmEnabled);
            TakeoffCommand = new RelayCommand(ExecuteTakeoff, () => IsTakeoffEnabled);
            LandingCommand = new RelayCommand(ExecuteLanding, () => IsLandingEnabled);
            RtlCommand = new RelayCommand(ExecuteRtl, () => IsRtlEnabled);

            UndoWaypointCommand = new RelayCommand(ExecuteUndoWaypoint, () => IsUndoEnabled);
            ClearWaypointsCommand = new RelayCommand(ExecuteClearWaypoints, () => IsClearEnabled);

            StrikeCommand = new RelayCommand(ExecuteStrike, () => IsStrikeEnabled);
            StopStrikeCommand = new RelayCommand(ExecuteStopStrike, () => IsStopStrikeEnabled);

            ShowHistoryCommand = new RelayCommand(async () => await ExecuteShowHistory());
            ConnectPx4MavlinkCommand = new RelayCommand(async () => await ExecuteConnectPx4Mavlink());


            InitializeAltitudeCommands();
            InitializeSpeedCommands();
        }

        // 목표 고도 증가/감소 커맨드를 초기화한다.
        private void InitializeAltitudeCommands()
        {
            IncreaseAltitudeCommand = new RelayCommand(async () =>
            {
                if (Drone.TargetAltitude >= 100) return;

                Drone.TargetAltitude += 1.0;

                if (Drone.IsArmed && (FlightMode == MODE_TAKEOFF || FlightMode == MODE_HOLD))
                {
                    await _mavlinkService.ChangeTargetAltitude((float)Drone.TargetAltitude);
                }
            });

            DecreaseAltitudeCommand = new RelayCommand(async () =>
            {
                if (Drone.TargetAltitude <= 1) return;

                Drone.TargetAltitude -= 1.0;

                if (Drone.IsArmed && (FlightMode == MODE_TAKEOFF || FlightMode == MODE_HOLD))
                {
                    await _mavlinkService.ChangeTargetAltitude((float)Drone.TargetAltitude);
                }
            });
        }

        // 목표 속도 증가/감소 커맨드를 초기화한다.
        private void InitializeSpeedCommands()
        {
            IncreaseSpeedCommand = new RelayCommand(async () =>
            {
                if (Drone.TargetSpeed >= 20) return;

                Drone.TargetSpeed += 1.0;

                if (Drone.IsArmed && (FlightMode == MODE_TAKEOFF || FlightMode == MODE_MISSION))
                {
                    await _mavlinkService.ChangeTargetSpeed((float)Drone.TargetSpeed);
                }
            });

            DecreaseSpeedCommand = new RelayCommand(async () =>
            {
                if (Drone.TargetSpeed <= 1) return;

                Drone.TargetSpeed -= 1.0;

                if (Drone.IsArmed && (FlightMode == MODE_TAKEOFF || FlightMode == MODE_MISSION))
                {
                    await _mavlinkService.ChangeTargetSpeed((float)Drone.TargetSpeed);
                }
            });
        }

        // 타겟 소실 감지 타이머를 초기화한다.
        private void InitializeTargetLostTimer()
        {
            _targetLostTimer = new System.Timers.Timer(3000);

            _targetLostTimer.Elapsed += (s, e) =>
            {
                if (IsStriking)
                {
                    Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        IsStriking = false;
                        FlightMode = MODE_HOLD;
                        AppendLog("[경고] 타겟을 놓쳤습니다. 추적을 중단하고 대기합니다.");
                    });
                }

                _targetLostTimer.Stop();
            };
        }

        // 화면에 표시되는 현재 시간 타이머를 초기화한다.
        private void InitializeClockTimer()
        {
            _clockTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };

            _clockTimer.Tick += (s, e) =>
            {
                CurrentDateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            };

            CurrentDateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _clockTimer.Start();
        }

        // 이번 프로그램 실행에서 사용할 비행 세션 ID를 초기화한다.
        private void InitializeFlightSession()
        {
            using (var db = new AppDbContext())
            {
                int lastId = db.FlightLogs.Any()
                    ? db.FlightLogs.Max(x => x.FlightId)
                    : 0;

                _currentFlightId = lastId + 1;
            }

            System.Diagnostics.Debug.WriteLine($"[세션] 이번 비행 아이디는 {_currentFlightId}입니다.");
        }
        #endregion


        #region [15. 보조 메서드]
        // 조회 날짜가 바뀌면 지도에 표시 중인 과거 비행 궤적을 초기화한다.
        private void ResetHistoryPathWhenDateChanged()
        {
            if (!_isHistoryVisible) return;

            _isHistoryVisible = false;

            HistoryPathRequested?.Invoke(new List<Tuple<double, double>>());

            Application.Current.Dispatcher.Invoke(() =>
                AppendLog("[시스템] 조회 날짜가 변경되어 지도의 궤적을 초기화합니다."));
        }

        // PX4 연결 실행 메서드
        private async Task ExecuteConnectPx4Mavlink()
        {
            try
            {
                string myGcsIp = "192.168.0.46"; // 네 Windows GCS IP

                await _ubuntuCommandService.StartMavlinkToGcsAsync(myGcsIp);

                AppendLog($"[PX4] Ubuntu PX4에 MAVLink 연결 명령 전송 완료: {myGcsIp}");
            }
            catch (Exception ex)
            {
                AppendLog($"[PX4][오류] MAVLink 자동 연결 실패: {ex.Message}");
            }
        }
        #endregion


        #region [16. 리소스 정리]
        // ViewModel에서 사용한 타이머와 통신 서비스를 정리한다.
        public void Dispose()
        {
            _targetLostTimer?.Stop();
            _targetLostTimer?.Dispose();

            _clockTimer?.Stop();
            _clockTimer = null;

            _ = _mavlinkService?.StopAsync();
            _ = _yoloMetaUdpService?.StopAsync();
        }
        #endregion
    }
}