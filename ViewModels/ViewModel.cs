using Asv.Mavlink;
using Asv.Mavlink.Minimal;
using GCS_Project.Models;
using GCS_Project.xServices;
using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Newtonsoft.Json;
using System.Net.Http;
using System.Text;

namespace GCS_Project.ViewModels
{
    public partial class ViewModel : ObservableObject // 명령 조율 역할 
    {
        #region [1. 필드 (Fields)]
        private readonly YoloMetaUdpService _yoloMetaUdpService;
        private readonly ObservableCollection<DetectedTarget> _detectedTargets = new();
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
        //scan 03/16
        private CancellationTokenSource? _scanCts;
        private bool _isScanning = false;

        private const string MODE_IDLE = "IDLE";
        private const string MODE_TAKEOFF = "TAKEOFF";
        private const string MODE_MISSION = "MISSION";
        private const string MODE_TRACKING = "TRACKING";
        #endregion


        #region [2. 속성 (Properties)]
        public MavlinkService MavlinkService => _mavlinkService;
        public DroneState Drone => _mavlinkService.CurrentState;

        // 현재는 MavlinkService의 타겟 리스트를 그대로 노출
        // 나중에 YoloMetaUdpService로 바꿔도 View/XAML 수정 없이 유지하기 위한 중간 창구
        public ObservableCollection<DetectedTarget> DetectedTargets => _detectedTargets;

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


        // 버튼 활성화 조건들 
        // 버튼 활성화 조건들 (매직 스트링 제거 완벽 적용!)
        public bool IsMissionStartEnabled => (FlightMode == MODE_TAKEOFF) && _currentWaypointCount > 0 && Drone.IsConnected && Drone.IsArmed;
        public bool IsTakeoffEnabled => (FlightMode == MODE_IDLE) && Drone.IsConnected && Drone.IsArmed;
        public bool IsLandingEnabled => (FlightMode == MODE_TAKEOFF && FlightMode != MODE_MISSION) && Drone.IsConnected && Drone.IsArmed;
        public bool IsRtlEnabled => (FlightMode == MODE_TAKEOFF || FlightMode == MODE_MISSION || FlightMode == MODE_TRACKING) && Drone.IsConnected && Drone.IsArmed;
        public bool IsUndoEnabled => (FlightMode == MODE_IDLE || FlightMode == MODE_TAKEOFF) && _currentWaypointCount > 0 && FlightMode != MODE_MISSION;
        public bool IsClearEnabled => (FlightMode == MODE_IDLE || FlightMode == MODE_TAKEOFF) && _currentWaypointCount > 0 && FlightMode != MODE_MISSION;
        public bool IsFollowEnabled => SelectedTarget != null && (FlightMode != MODE_IDLE && FlightMode != MODE_TRACKING) && Drone.IsConnected && Drone.IsArmed;
        public bool IsStopEnabled => (FlightMode == MODE_MISSION || FlightMode == MODE_TRACKING);
        public bool IsArmEnabled => (FlightMode == MODE_IDLE) && Drone.IsConnected && !Drone.IsArmed;
        public bool IsDisarmEnabled => FlightMode == MODE_IDLE && Drone.IsConnected && Drone.IsArmed;
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
        // 고도, 속도 조절 명령
        public ICommand IncreaseAltitudeCommand { get; }
        public ICommand DecreaseAltitudeCommand { get; }
        public ICommand IncreaseSpeedCommand { get; }
        public ICommand DecreaseSpeedCommand { get; }
        public ICommand SendAICommand { get; }
        #endregion


        #region [4. 이벤트 (View 통신용)]
        public event Action? UndoWaypointRequested;
        public event Action? ClearWaypointsRequested;
        public event Action? AllClearRequested;
        public event Action<double, double, double>? PositionUpdated;
        public event Action<double, double>? TakeoffPointSet;
        public event Action<double, double>? PersonDetectedOnMap;
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
            SendAICommand = new RelayCommand(
                async () => await ExecuteSendAICommand(),
                () => !string.IsNullOrWhiteSpace(UserInputCommand)
            );

            IncreaseAltitudeCommand = new RelayCommand(async () =>
            {
                if (Drone.TargetAltitude < 100)
                {
                    Drone.TargetAltitude += 1.0;

                    // 안전장치: 시동이 걸려있고 && TAKEOFF 모드일 때만 명령 전송!
                    if (Drone.IsArmed && FlightMode == MODE_TAKEOFF)
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

                    if (Drone.IsArmed && FlightMode == MODE_TAKEOFF)
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

                    if (Drone.IsArmed && FlightMode == MODE_TAKEOFF)
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

                    if (Drone.IsArmed && FlightMode == MODE_TAKEOFF)
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
        }
        #endregion


        public void Dispose()
        {
            _targetLostTimer?.Stop();
            _targetLostTimer?.Dispose();
            _mavlinkService?.StopAsync();
            _yoloMetaUdpService?.StopAsync();
        }
    }
}