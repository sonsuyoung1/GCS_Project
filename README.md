<img width="1200" height="900" alt="image" src="https://github.com/user-attachments/assets/fa4eaa53-df9c-4ebc-82a5-2259182f41eb" /># GCS Dashboard

PX4 기반 드론을 위한 Windows용 지상 관제 시스템(GCS, Ground Control Station)입니다. WPF로 제작되었으며, 드론의 실시간 비행 제어, 영상 스트리밍 기반 객체 탐지, 지상 컴패니언 컴퓨터(Ubuntu/ROS2) 연동을 하나의 대시보드에서 처리합니다.

## 주요 기능

- **비행 제어**: Arm/Disarm, Takeoff, Landing, RTL(귀환), 미션 시작/중단, 고도·속도 조절
- **웨이포인트 미션**: 지도 클릭으로 경로 지정, 실행 취소(Undo)/초기화, 이동 경로 실시간 표시
- **실시간 텔레메트리**: 위치, 고도, 속도, 배터리, Roll/Pitch/Yaw, 연결 상태를 지도·계기판에 실시간 반영
- **영상 스트리밍 및 객체 탐지**: LibVLC로 RTP 영상을 수신하고, YOLO가 UDP로 보내온 탐지 결과를 영상 위에 바운딩 박스로 오버레이
- **타겟 추적/타격 명령**: 탐지된 타겟 선택, 추적(Follow), 타격(Strike) 명령을 ROS2 컴패니언 컴퓨터로 전송
- **비행 기록 저장**: 비행 로그·타겟 탐지 로그를 SQLite(EF Core)에 저장하고 이력 조회 가능

## 기술 스택

| 영역 | 사용 기술 |
|---|---|
| UI | WPF (.NET 9, C#), MVVM |
| 드론 통신 | MAVLink ([Asv.Mavlink](https://github.com/asv-soft/asv-mavlink)) |
| 지도 | WebView2 + Leaflet 기반 `map.html` (JS ↔ C# 브리지) |
| 영상 | LibVLCSharp (RTP 스트림 재생) |
| 객체 탐지 연동 | YOLO 메타데이터 UDP 수신 |
| 데이터 저장 | EF Core + SQLite |

## 아키텍처

```
<img width="1200" height="900" alt="image" src="https://github.com/user-attachments/assets/de37102b-9f58-4c56-a7e1-0a6cc238ac4c" />

┌───────────────────┐        MAVLink (UDP)        ┌──────────────┐
│   GCS Dashboard    │ ───────────────────────────▶│  PX4 (드론)  │
│   (Windows, WPF)   │◀─────────────────────────── │              │
└─────────┬──────────┘        Telemetry            └──────────────┘
          │  UDP (YOLO 메타데이터 / ROS2 명령)
          ▼
┌───────────────────────────┐
│ Ubuntu 컴패니언 컴퓨터     │
│ (ROS2, MAVLink 브릿지)    │
└───────────────────────────┘
```

## 프로젝트 구조

```
GCS_Project/
├─ Views/                 # XAML 화면 및 MainWindow partial 클래스
├─ ViewModels/             # MVVM ViewModel (기능별 partial 클래스로 분리)
├─ Services/               # MavlinkService, MapController, YOLO/SSH 연동 서비스
├─ Models/                 # DroneState, DetectedTarget 등 도메인 모델
├─ Data/                   # EF Core DbContext, 로그 테이블 정의
├─ Migrations/             # EF Core 마이그레이션
└─ map.html                # 지도 표시 및 드론/타겟 마커, 경로 렌더링(JS)
```

## 실행 방법

1. Windows + .NET 9 SDK 설치
2. PX4(SITL 또는 실기체)와 통신 가능한 네트워크 환경 준비
3. `GCS_Project.csproj`의 `dotnet restore` 후 빌드

```bash
dotnet build GCS_Project.csproj
```

4. 실행 후 PX4 MAVLink 연결 정보(IP/Port)를 설정하면 텔레메트리 수신이 시작됩니다.

## 트러블슈팅

개발 중 겪었던 주요 문제(수동 조종 종료 후 RTL 오작동, 연결 끊김 후 미재연결, 텔레메트리 UI 성능 저하, 영상·YOLO 시간 동기화 등)와 해결 과정은 [Troubleshooting.md](./Troubleshooting.md)에 정리했습니다.
