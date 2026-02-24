using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace GCS_Project.Models
{
    public class DroneState : ObservableObject
    {
        // 1. 필드 선언
        private double _latitude;
        private double _longitude;
        private double _altitude;
        private double _speed;
        private bool _isConnected;
        private double? _takeoffLat;
        private double? _takeoffLng;
        private int _batteryPercent; // 추가된 배터리 필드
        private bool _isArmed;

        // --- HUD(인공지평선/나침반)를 위한 추가 필드 ---
        private double _pitch;      // 기체의 앞뒤 기울기
        private double _roll;       // 기체의 좌우 회전
        private double _heading;    // 기체가 바라보는 방위 (Yaw)

        private bool _isTargetDetected;
        private double _targetLat;
        private double _targetLng;

        // 2. 속성 선언 (SetProperty 방식 적용)
        public double Latitude { get => _latitude; set => SetProperty(ref _latitude, value); }
        public double Longitude { get => _longitude; set => SetProperty(ref _longitude, value); }
        public double Altitude { get => _altitude; set => SetProperty(ref _altitude, value); }
        public double Speed { get => _speed; set => SetProperty(ref _speed, value); }
        public bool IsConnected { get => _isConnected; set => SetProperty(ref _isConnected, value); }
        public double? TakeoffLat { get => _takeoffLat; set => SetProperty(ref _takeoffLat, value); }
        public double? TakeoffLng { get => _takeoffLng; set => SetProperty(ref _takeoffLng, value); }
        public int BatteryPercent { get => _batteryPercent; set => SetProperty(ref _batteryPercent, value); }
        public bool IsArmed { get => _isArmed; set => SetProperty(ref _isArmed, value); }

        // --- HUD 속성 (SetProperty를 통해 UI에 실시간 반영) ---
        public double Pitch { get => _pitch; set => SetProperty(ref _pitch, value); }
        public double Roll { get => _roll; set => SetProperty(ref _roll, value); }
        public double Heading { get => _heading; set => SetProperty(ref _heading, value); }

        public bool IsTargetDetected { get => _isTargetDetected; set => SetProperty(ref _isTargetDetected, value); }
        public double TargetLat { get => _targetLat; set => SetProperty(ref _targetLat, value); }
        public double TargetLng { get => _targetLng; set => SetProperty(ref _targetLng, value); }
    }
}
