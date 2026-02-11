using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GCS_Project.xServices
{
    public static class MavlinkCRC
    {
        public static ushort AccumulateCRC(byte data, ushort crc)
        {
            unchecked
            {
                byte tmp = (byte)(data ^ (byte)(crc & 0xff));
                tmp ^= (byte)(tmp << 4);
                ushort result = (ushort)((crc >> 8) ^ (tmp << 8) ^ (tmp << 3) ^ (tmp >> 4));
                return result;
            }
        }

        public static ushort CalculateCRC(byte[] data, int start, int length)
        {
            ushort crc = 0xFFFF; // 초기값
            for (int i = start; i < start + length; i++)
            {
                crc = AccumulateCRC(data[i], crc);
            }
            return crc;
        }
    }
}
