using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GCS_Project.Models
{
    public class GeoPoint
    {
        // 위도 (Latitude)
        public double Lat { get; set; }

        // 경도 (Longitude)
        public double Lng { get; set; }

        // 고도 (Altitude) - 미션이나 드론 상태 확인 시 필요할 수 있어 추가해둡니다.
        public double Alt { get; set; }

        // 기본 생성자
        public GeoPoint() { }

        // 편의를 위한 생성자
        public GeoPoint(double lat, double lng)
        {
            Lat = lat;
            Lng = lng;
        }

        // 디버깅 시 좌표를 쉽게 확인하기 위함
        public override string ToString()
        {
            return $"{Lat:F6}, {Lng:F6} (Alt: {Alt:F1}m)";
        }
    }
}
