using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace GCS_Project.Models
{
    public class DetectedTarget : INotifyPropertyChanged
    {
        private double _lat;
        private double _lng;
        private bool _isBeingTracked;
        private double _bx, _by, _bw, _bh;

        // 1. 객체 고유 ID (YOLO/Tracker에서 부여한 번호)
        public int ID { get; set; }

        // 2. 객체 종류 (예: Person, Car 등)
        public string TargetType { get; set; } = "Unknown";

        // 3. 위도 (실시간 갱신)
        public double Lat
        {
            get => _lat;
            set { _lat = value; OnPropertyChanged(); }
        }

        // 4. 경도 (실시간 갱신)
        public double Lng
        {
            get => _lng;
            set { _lng = value; OnPropertyChanged(); }
        }

        // 5. 현재 이 객체를 추적 중인지 여부
        public bool IsBeingTracked
        {
            get => _isBeingTracked;
            set { _isBeingTracked = value; OnPropertyChanged(); }
        }

        // ===== YOLO BBOX 위치 저장=====
        public double Bx { get => _bx; set { _bx = value; OnPropertyChanged(); } }
        public double By { get => _by; set { _by = value; OnPropertyChanged(); } }
        public double Bw { get => _bw; set { _bw = value; OnPropertyChanged(); } }
        public double Bh { get => _bh; set { _bh = value; OnPropertyChanged(); } }

        // UI 갱신을 위한 인터페이스 구현
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
