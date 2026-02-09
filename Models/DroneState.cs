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
        private double _latitude; // 드론의 상태 값 (위도, 경도, 고도, 속도, 연결 유무)
        private double _longitude;
        private double _altitude;
        private double _speed;
        private bool _isConnected;
        private double? _takeoffLat; // 이륙한 위치의 위도, 경도
        private double? _takeoffLng;

        public double Latitude { get => _latitude; set { _latitude = value; OnPropertyChanged(); } }
        public double Longitude { get => _longitude; set { _longitude = value; OnPropertyChanged(); } }
        public double Altitude { get => _altitude; set { _altitude = value; OnPropertyChanged(); } }
        public double Speed { get => _speed; set { _speed = value; OnPropertyChanged(); } }
        public bool IsConnected { get => _isConnected; set { _isConnected = value; OnPropertyChanged(); } }
        public double? TakeoffLat { get => _takeoffLat; set { _takeoffLat = value; OnPropertyChanged(); } }
        public double? TakeoffLng { get => _takeoffLng; set { _takeoffLng = value; OnPropertyChanged(); } }

        private bool _isTargetDetected; // 객체 탐지 값 (탐지 유무, 타켓 위도, 경도)
        private double _targetLat;
        private double _targetLng;

        public bool IsTargetDetected { get => _isTargetDetected; set { _isTargetDetected = value; OnPropertyChanged(); } }
        public double TargetLat { get => _targetLat; set { _targetLat = value; OnPropertyChanged(); } }
        public double TargetLng { get => _targetLng; set { _targetLng = value; OnPropertyChanged(); } }

        
    }
    public class TargetModel
    {
        public double Lat { get; set; }
        public double Lng { get; set; }
        public string FoundTime { get; set; }
    }
}
