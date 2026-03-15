using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GCS_Project.Models
{
    public class AiCommandResponse
    {
        public string Action { get; set; }  // TAKEOFF, LAND, RTL, STRIKE, NONE 등
        public double Value { get; set; }   // 고도 10m 등 숫자가 필요할 때
        public string Message { get; set; } // 사용자에게 보여줄 친절한 대답
    }
}
