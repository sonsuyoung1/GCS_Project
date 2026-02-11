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
        public bool IsTargetDetected { get => _isTargetDetected; set => SetProperty(ref _isTargetDetected, value); }
        public double TargetLat { get => _targetLat; set => SetProperty(ref _targetLat, value); }
        public double TargetLng { get => _targetLng; set => SetProperty(ref _targetLng, value); }
    }
}
