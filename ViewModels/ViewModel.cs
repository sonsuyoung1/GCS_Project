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
        private readonly MavlinkService _mavlinkService;
        private DetectedTarget? _selectedTarget;
        private System.Timers.Timer _targetLostTimer;
        private string _flightMode = "IDLE";
        private string _chatLog = "드론 연결 대기 중...";
        private int _currentWaypointCount = 0;
        private double _pitchOffset;
        private double _headingOffset;
        private bool _mavStarting;
        private string _currentDateTime = "";
        private bool _isFlyingHigh = false;
        private bool _isTakeoffMarkerDrawn = false;
        private readonly List<GeoPoint> _waypoints = new List<GeoPoint>();
        private DateTime _lastMapUpdate = DateTime.MinValue;
        private string _userInputCommand = "";
        private bool _isHistoryVisible = false;
        private int _currentFlightId;

        private const string MODE_IDLE = "IDLE";
        private const string MODE_TAKEOFF = "TAKEOFF";
        private const string MODE_MISSION = "MISSION";
        private const string MODE_TRACKING = "TRACKING";
        private const string MODE_HOLD = "HOLD";
        #endregion


        #region [2. 속성 (Properties)]
        public MavlinkService MavlinkService => _mavlinkService;
        public DroneState Drone => _mavlinkService.CurrentState;

        // MavlinkService의 리스트를 직접 참조하도록 변경하여 메모리 중복 방지
        // public ObservableCollection<DetectedTarget> DetectedTargets => _mavlinkService.DetectedTargets;

        public string FlightMode
        {
            get => _flightMode;
            set { if (SetProperty(ref _flightMode, value)) NotifyCommandStates(); }
        }
        public string ChatLog
        {
            get => _chatLog;
            set => SetProperty(ref _chatLog, value);
        }
        public string UserInputCommand
        {
            get => _userInputCommand;
            set => SetProperty(ref _userInputCommand, value);
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
                    (FollowCommand as RelayCommand)?.NotifyCanExecuteChanged();
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


        // 버튼 활성화 조건
        // 1. 시동 제어
        public bool IsArmEnabled => (FlightMode == MODE_IDLE) && Drone.IsConnected && !Drone.IsArmed;
        public bool IsDisarmEnabled => (FlightMode == MODE_IDLE) && Drone.IsConnected && Drone.IsArmed;

        // 2. 이륙: 땅에 있고(IDLE), 시동이 걸려 있을 때만 가능
        public bool IsTakeoffEnabled => (FlightMode == MODE_IDLE) && Drone.IsConnected && Drone.IsArmed;

        // 3. 미션 시작: 공중에 떠 있을 때(이륙 중이거나 제자리 비행 중) 웨이포인트가 있으면 가능
        public bool IsMissionStartEnabled => (FlightMode == MODE_TAKEOFF || FlightMode == MODE_HOLD) && _currentWaypointCount > 0 && Drone.IsConnected && Drone.IsArmed;

        // 4. 착륙: 공중에 떠 있을 때(미션 중이 아닐 때) 언제든 가능
        public bool IsLandingEnabled => (FlightMode == MODE_TAKEOFF || FlightMode == MODE_HOLD) && Drone.IsConnected && Drone.IsArmed;

        // 5. 복귀(RTL): 비행 중, 미션 중, 타격(추적) 중 언제라도 비상 호출 가능!
        public bool IsRtlEnabled => (FlightMode == MODE_TAKEOFF || FlightMode == MODE_HOLD || FlightMode == MODE_MISSION || FlightMode == MODE_TRACKING) && Drone.IsConnected && Drone.IsArmed;

        // 6. 타겟 추적/타격 (Follow/Strike): 타겟이 선택되어 있고, 공중에 떠 있으며, 이미 타격 중이 아닐 때
        public bool IsFollowEnabled => SelectedTarget != null && (FlightMode == MODE_TAKEOFF || FlightMode == MODE_HOLD || FlightMode == MODE_MISSION) && Drone.IsConnected && Drone.IsArmed;

        // 7. 정지(Stop): 미션 중이거나 타격(추적) 중일 때 기체를 멈추고 HOLD 모드로 전환
        public bool IsStopEnabled => (FlightMode == MODE_MISSION || FlightMode == MODE_TRACKING) && Drone.IsConnected;

        // 8. 웨이포인트 편집: 미션이나 타격 중이 아닐 때(대기, 이륙, 홀드 상태) 자유롭게 편집 가능
        public bool IsUndoEnabled => (FlightMode == MODE_IDLE || FlightMode == MODE_TAKEOFF || FlightMode == MODE_HOLD) && _currentWaypointCount > 0;
        public bool IsClearEnabled => (FlightMode == MODE_IDLE || FlightMode == MODE_TAKEOFF || FlightMode == MODE_HOLD) && _currentWaypointCount > 0;
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

            // MavlinkService가 드론 모드 변경을 감지하면 내 FlightMode를 업데이트!
            _mavlinkService.OnFlightModeChanged += (newMode) =>
            {
                // UI 스레드에서 안전하게 업데이트 (WPF 특성상 Dispatcher 필요할 수 있음)
                Application.Current.Dispatcher.Invoke(() =>
                {
                    FlightMode = newMode; // SetProperty가 실행되면서 UI와 버튼 상태가 자동 갱신됨
                    ChatLog += $"\n[시스템] 기체 비행 모드 변경 확인: {newMode}";
                });
            };

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
            SendAICommand = new RelayCommand(async () => await ExecuteSendAICommand());
            ShowHistoryCommand = new RelayCommand(async () => await ExecuteShowHistory());
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
                if (FlightMode == "TRACKING")
                {
                    Application.Current.Dispatcher.Invoke(() => {
                        FlightMode = "TAKEOFF";
                        ChatLog += "\n[경고] 타겟을 놓쳤습니다. 추적을 중단하고 대기합니다.";
                    });
                }
                _targetLostTimer.Stop();
            };


            // 생성자 안에서 시계 타이머를 돌려줍니다.
            System.Windows.Threading.DispatcherTimer clockTimer = new System.Windows.Threading.DispatcherTimer();
            clockTimer.Interval = TimeSpan.FromSeconds(1); // 1초마다 실행
            clockTimer.Tick += (s, e) =>
            {
                // "2024-05-18 14:30:45" 형태로 1초마다 업데이트
                CurrentDateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            };
            CurrentDateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); // 시작하자마자 한 번 세팅
            clockTimer.Start();

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
            _mavlinkService?.StopAsync();
        }
    }
}