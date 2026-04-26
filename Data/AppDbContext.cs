using Microsoft.EntityFrameworkCore;
using System;
using System.IO;

namespace GCS_Project.Data
{
    // 1. DB와 C#을 연결하는 핵심 다리 (DbContext)
    public class AppDbContext : DbContext
    {
        // 3개의 테이블을 선언합니다.
        public DbSet<FlightLog> FlightLogs { get; set; }
        public DbSet<TargetLog> TargetLogs { get; set; }
        public DbSet<SystemLog> SystemLogs { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // 프로그램 실행 폴더에 "gcs_database.db"라는 파일 이름으로 저장하겠다는 뜻!
            string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gcs_database.db");
            optionsBuilder.UseSqlite($"Data Source={dbPath}");
        }
    }

    // 2. [테이블 1] 드론 비행 블랙박스
    public class FlightLog
    {
        public int Id { get; set; } // 고유 번호 (자동 증가)
        public int FlightId { get; set; }
        public DateTime Timestamp { get; set; } // 기록 시간
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Altitude { get; set; }
        public double Speed { get; set; }
        public int BatteryPercent { get; set; }
        public string FlightMode { get; set; } = string.Empty;
    }

    // 3. [테이블 2] YOLO 적군 탐지 기록
    public class TargetLog
    {
        public int Id { get; set; }
        public int FlightId { get; set; }
        public int TargetId { get; set; }
        public DateTime Timestamp { get; set; }
        public string TargetType { get; set; } = string.Empty; // 예: "Tank", "Vehicle"
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public bool IsStriked { get; set; } // 타격(요격) 완료 여부
    }

    // 4. [테이블 3] AI 및 시스템 통신 기록
    public class SystemLog
    {
        public int Id { get; set; }
        public DateTime Timestamp { get; set; }
        public string Sender { get; set; } = string.Empty; // "USER", "AI", "SYSTEM"
        public string Message { get; set; } = string.Empty;
    }
}