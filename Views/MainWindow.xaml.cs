using GCS_Project.Models;
using GCS_Project.Services;
using GCS_Project.ViewModels;
using LibVLCSharp.Shared;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace GCS_Project
{
    public partial class MainWindow : Window
    {
        #region [1. 공통 필드]

        private MapController? _mapController;
        private ViewModel? _viewModel;

        private LibVLC? _libVLC;
        private MediaPlayer? _mp1;

        private OverlayWindow? _overlayWindow;
        private System.Windows.Threading.DispatcherTimer? _overlayTimer;

        private bool _mapBridgeInitialized = false;
        private bool _overlayDirty = false;
        private int? _selectedTargetId = null;


        private const double FRAME_WIDTH = 640.0;
        private const double FRAME_HEIGHT = 480.0;

        private short _joyPitch = 0;
        private short _joyRoll = 0;
        private short _joyThrust = 500;
        private short _joyYaw = 0;

        #endregion

        #region [2. 생성자]
        public MainWindow()
        {
            InitializeComponent();

            InitializeViewModel();
            InitializeVideoPlayer();
            SubscribeWindowEvents();
        }
        #endregion


        #region [3. 초기화]
        // ViewModel을 생성하고 DataContext 및 타겟 변경 이벤트를 연결한다.
        private void InitializeViewModel()
        {
            _viewModel = new ViewModel();
            DataContext = _viewModel;

            _viewModel.DetectedTargets.CollectionChanged += DetectedTargets_CollectionChanged;

            foreach (var target in _viewModel.DetectedTargets)
            {
                target.PropertyChanged += DetectedTarget_PropertyChanged;
            }
        }

        // 윈도우 생명주기 및 크기 변경 이벤트를 연결한다.
        private void SubscribeWindowEvents()
        {
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;

            LocationChanged += MainWindow_WindowChanged;
            SizeChanged += MainWindow_WindowChanged;
            StateChanged += MainWindow_WindowChanged;
        }

        // 창 로드 후 지도와 오버레이를 초기화한다.
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null)
            {
                MessageBox.Show("ViewModel(DataContext) 연결이 안 됨");
                return;
            }

            await InitializeMapAsync();

            PlayRtpStream(_mp1);

            InitializeOverlayWindow();
            InitializeOverlayTimer();

            await Dispatcher.InvokeAsync(
                UpdateOverlayBounds,
                System.Windows.Threading.DispatcherPriority.Loaded
            );
        }

        #endregion
    }
}