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
        private int _batteryPercent;
        private bool _isArmed;
        private bool _isTargetDetected;
        private double _targetLat;
        private double _targetLng;
        private double _roll;
        private double _pitch;
        private double _yaw;
        private double _targetAltitude = 5.0;
        private double _targetSpeed = 2.0;


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

        public double Roll { get => _roll; set => SetProperty(ref _roll, value); }
        public double Pitch { get => _pitch; set => SetProperty(ref _pitch, value); }
        public double Yaw { get => _yaw; set => SetProperty(ref _yaw, value); }

        public bool IsTargetDetected { get => _isTargetDetected; set => SetProperty(ref _isTargetDetected, value); }
        public double TargetLat { get => _targetLat; set => SetProperty(ref _targetLat, value); }
        public double TargetLng { get => _targetLng; set => SetProperty(ref _targetLng, value); }

        public double TargetAltitude { get => _targetAltitude; set => SetProperty(ref _targetAltitude, value); }
        public double TargetSpeed { get => _targetSpeed; set => SetProperty(ref _targetSpeed, value); }
    }
}
