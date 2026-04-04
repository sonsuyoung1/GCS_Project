using Asv.Mavlink;
using Asv.Mavlink.Minimal;
using GCS_Project.Data;
using GCS_Project.Models;
using GCS_Project.xServices;
using Newtonsoft.Json;
using System;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace GCS_Project.ViewModels
{
    public partial class ViewModel : ObservableObject // 명령 조율 역할 
    {
        #region [1. 필드 (Fields)]
        private readonly YoloMetaUdpService _yoloMetaUdpService;
        private readonly ObservableCollection<DetectedTarget> _detectedTargets = new();
        private readonly MavlinkService _mavlinkService;
        private readonly DroneState _uiDrone = new DroneState();
        private DetectedTarget? _selectedTarget;
        private System.Timers.Timer _targetLostTimer;
        private string _flightMode = MODE_IDLE;
        private string _chatLog = "드론 연결 대기 중...";
        private readonly StringBuilder _chatLogBuffer = new StringBuilder("드론 연결 대기 중...");
        private int _currentWaypointCount = 0;
        private double _pitchOffset;
        private double _headingOffset;
        private bool _mavStarting;
        private string _currentDateTime = "";
        private bool _isFlyingHigh = false;
        private bool _isTakeoffMarkerDrawn = false;
        private readonly List<GeoPoint> _waypoints = new List<GeoPoint>();
        private DateTime _lastMapUpdate = DateTime.MinValue;
        private readonly DateTime _programStartTime = DateTime.Now;
        private DateTime? _selectedHistoryDate = DateTime.Today;
        private bool _isHistoryVisible = false;
        private string _userInputCommand = "";
        //scan 03/16
        private CancellationTokenSource? _scanCts;
        private bool _isScanning = false;
        private bool _batteryWarningShown = false;
        private const string MODE_IDLE = "IDLE";
        private const string MODE_TAKEOFF = "TAKEOFF";
        private const string MODE_MISSION = "MISSION";
        private const string MODE_TRACKING = "TRACKING";
        private const string MODE_MANUAL = "MANUAL";
        private const string MODE_HOLD = "HOLD";
        private int _currentFlightId;
        //버튼 상태에 영향을 주는 값만 따로 스냅샷으로 묶고, 그 스냅샷이 바뀔 때만 NotifyCommandStates()를 호출
        private sealed record CommandStateSnapshot(
            string FlightMode,
            bool IsConnected,
            bool IsArmed,
            int WaypointCount,
            bool HasSelectedTarget
        );
        private CommandStateSnapshot? _lastCommandStateSnapshot;

        private System.Windows.Threading.DispatcherTimer? _clockTimer;
        #endregion


        #region [2. 속성 (Properties)]
        public MavlinkService MavlinkService => _mavlinkService;
        public DroneState Drone => _uiDrone;

        // 현재는 MavlinkService의 타겟 리스트를 그대로 노출
        // 나중에 YoloMetaUdpService로 바꿔도 View/XAML 수정 없이 유지하기 위한 중간 창구
        public ObservableCollection<DetectedTarget> DetectedTargets => _detectedTargets;

        // MavlinkService의 리스트를 직접 참조하도록 변경하여 메모리 중복 방지
        // public ObservableCollection<DetectedTarget> DetectedTargets => _mavlinkService.DetectedTargets;

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
        public string UserInputCommand
        {
            get => _userInputCommand;
            set
            {
                if (SetProperty(ref _userInputCommand, value))
                {
                    (SendAICommand as RelayCommand)?.NotifyCanExecuteChanged();
                }
            }
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
                    // 사용자가 달력에서 다른 날짜를 클릭했을 때,
                    // 이미 지도에 궤적이 켜져 있다면 과거 궤적과 헷갈리지 않도록 싹 지워줍니다.
                    if (_isHistoryVisible)
                    {
                        _isHistoryVisible = false;

                        // 빈 리스트를 보내서 지도의 선을 지우라고 View(화면)에 명령합니다.
                        HistoryPathRequested?.Invoke(new List<Tuple<double, double>>());

                        // UI 스레드 안전하게 로그 출력
                        Application.Current.Dispatcher.Invoke(() =>
                            AppendLog("[시스템] 조회 날짜가 변경되어 지도의 궤적을 초기화합니다."));
                    }
                }
            }
        }


        // 버튼 활성화 조건들 
        public bool IsArmEnabled =>
            (FlightMode == MODE_IDLE) && Drone.IsConnected && !Drone.IsArmed;

        public bool IsDisarmEnabled =>
            (FlightMode == MODE_IDLE) && Drone.IsConnected && Drone.IsArmed;

        public bool IsTakeoffEnabled =>
            (FlightMode == MODE_IDLE) && Drone.IsConnected && Drone.IsArmed;

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
            (FlightMode == MODE_TAKEOFF || FlightMode == MODE_HOLD || FlightMode == MODE_MISSION || FlightMode == MODE_TRACKING) &&
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

        public bool IsFollowEnabled =>
            SelectedTarget != null &&
            (FlightMode == MODE_TAKEOFF || FlightMode == MODE_HOLD || FlightMode == MODE_MISSION) &&
            Drone.IsConnected &&
            Drone.IsArmed;

        public bool IsStopEnabled =>
            (FlightMode == MODE_MISSION || FlightMode == MODE_TRACKING) &&
            Drone.IsConnected;
        #endregion


        #region [3. 커맨드 (Commands)]
        public ICommand StartMissionCommand { get; }
        public ICommand StopMissionCommand { get; }
        public ICommand ArmCommand { get; }
        public ICommand DisarmCommand { get; }
        public ICommand TakeoffCommand { get; }
        public ICommand LandingCommand { get; }
        public ICommand RtlCommand { get; }
        public ICommand UndoWaypointCommand { get; }
        public ICommand ClearWaypointsCommand { get; }
        public ICommand FollowCommand { get; }
        // public ICommand StopTrackingCommand { get; }
        public ICommand IncreaseAltitudeCommand { get; }
        public ICommand DecreaseAltitudeCommand { get; }
        public ICommand IncreaseSpeedCommand { get; }
        public ICommand DecreaseSpeedCommand { get; }
        public ICommand SendAICommand { get; }
        public ICommand ShowHistoryCommand { get; }
        #endregion


        #region [4. 이벤트 (View 통신용)]
        public event Action? UndoWaypointRequested;
        public event Action? ClearWaypointsRequested;
        public event Action? AllClearRequested;
        public event Action<double, double, double>? PositionUpdated;
        public event Action<double, double>? TakeoffPointSet;
        public event Action<double, double>? PersonDetectedOnMap;
        public event Action<List<Tuple<double, double>>>? HistoryPathRequested;
        #endregion


        #region [5. 생성자 (Constructor)]
        public ViewModel()
        {
            _mavlinkService = new MavlinkService();
            _yoloMetaUdpService = new YoloMetaUdpService(5001);
            // 커맨드 초기화 (로직 분리)
            StartMissionCommand = new RelayCommand(ExecuteStartMission, () => IsMissionStartEnabled);
            ArmCommand = new RelayCommand(ExecuteArm, () => IsArmEnabled);
            DisarmCommand = new RelayCommand(ExecuteDisarm, () => IsDisarmEnabled);
            TakeoffCommand = new RelayCommand(ExecuteTakeoff, () => IsTakeoffEnabled);
            LandingCommand = new RelayCommand(ExecuteLanding, () => IsLandingEnabled);
            RtlCommand = new RelayCommand(ExecuteRtl, () => IsRtlEnabled);
            UndoWaypointCommand = new RelayCommand(ExecuteUndoWaypoint, () => IsUndoEnabled);
            ClearWaypointsCommand = new RelayCommand(ExecuteClearWaypoints, () => IsClearEnabled);
            FollowCommand = new RelayCommand(ExecuteFollow, () => IsFollowEnabled);
            StopMissionCommand = new RelayCommand(ExecuteStopMission, () => IsStopEnabled);
            // StopTrackingCommand = new RelayCommand(ExecuteStopTracking, () => IsStopEnabled);
            ShowHistoryCommand = new RelayCommand(async () => await ExecuteShowHistory());
            SendAICommand = new RelayCommand(
                async () => await ExecuteSendAICommand(),
                () => !string.IsNullOrWhiteSpace(UserInputCommand)
            );

            IncreaseAltitudeCommand = new RelayCommand(async () =>
            {
                if (Drone.TargetAltitude < 100)
                {
                    Drone.TargetAltitude += 1.0;

                    if (Drone.IsArmed && (FlightMode == MODE_TAKEOFF || FlightMode == MODE_HOLD))
                    {
                        await _mavlinkService.ChangeTargetAltitude((float)Drone.TargetAltitude);
                    }
                }
            });
            DecreaseAltitudeCommand = new RelayCommand(async () =>
            {
                if (Drone.TargetAltitude > 1)
                {
                    Drone.TargetAltitude -= 1.0;

                    if (Drone.IsArmed && (FlightMode == MODE_TAKEOFF || FlightMode == MODE_HOLD))
                    {
                        await _mavlinkService.ChangeTargetAltitude((float)Drone.TargetAltitude);
                    }
                }
            });
            IncreaseSpeedCommand = new RelayCommand(async () =>
            {
                if (Drone.TargetSpeed < 20)
                {
                    Drone.TargetSpeed += 1.0;

                    if (Drone.IsArmed && (FlightMode == MODE_TAKEOFF || FlightMode == MODE_MISSION))
                    {
                        await _mavlinkService.ChangeTargetSpeed((float)Drone.TargetSpeed);
                    }
                }
            });
            DecreaseSpeedCommand = new RelayCommand(async () =>
            {
                if (Drone.TargetSpeed > 1)
                {
                    Drone.TargetSpeed -= 1.0;

                    if (Drone.IsArmed && (FlightMode == MODE_TAKEOFF || FlightMode == MODE_MISSION))
                    {
                        await _mavlinkService.ChangeTargetSpeed((float)Drone.TargetSpeed);
                    }
                }
            });


            _targetLostTimer = new System.Timers.Timer(3000); // 3초 동안 데이터 없으면 소실 판단
            _targetLostTimer.Elapsed += (s, e) => {
                if (FlightMode == MODE_TRACKING)
                {
                    Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        FlightMode = MODE_TAKEOFF;
                        AppendLog("[경고] 타겟을 놓쳤습니다. 추적을 중단하고 대기합니다.");
                    });
                }
                _targetLostTimer.Stop();
            };


            // 생성자 안에서 시계 타이머를 돌려줍니다.
            _clockTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };

            _clockTimer.Tick += (s, e) =>
            {
                // "2024-05-18 14:30:45" 형태로 1초마다 업데이트
                CurrentDateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            };

            CurrentDateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _clockTimer.Start();

            // 서비스 시작 및 이벤트 구독
            InitializeService();
            InitializeFlightSession();
        }
        #endregion

        private void InitializeFlightSession()
        {
            using (var db = new AppDbContext())
            {
                // DB에서 가장 큰 FlightId를 찾아 +1 합니다. 없으면 1부터 시작.
                int lastId = db.FlightLogs.Any() ? db.FlightLogs.Max(x => x.FlightId) : 0;
                _currentFlightId = lastId + 1;
            }
            System.Diagnostics.Debug.WriteLine($"[세션] 이번 비행 아이디는 {_currentFlightId}입니다.");
        }
        public void Dispose()
        {
            _targetLostTimer?.Stop();
            _targetLostTimer?.Dispose();

            _clockTimer?.Stop();
            _clockTimer = null;

            _ = _mavlinkService?.StopAsync();
            _ = _yoloMetaUdpService?.StopAsync();
        }
    }
}