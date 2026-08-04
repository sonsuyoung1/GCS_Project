# Troubleshooting

개발 및 시뮬레이션(PX4 SITL/Gazebo) 테스트 과정에서 겪었던 주요 문제와 원인 분석, 해결 방법을 정리한 문서입니다.

---

## 1. 수동 조종 종료 시 드론이 RTL(Return To Launch)로 복귀하는 문제

### 문제

수동 조종(Manual Control) 모드를 활성화하여 드론을 직접 조종한 후 수동 조종 모드를 종료하면, 드론이 현재 위치에서 호버링(HOLD)하는 것이 아니라 자동으로 이륙 지점으로 복귀(RTL)하는 문제가 발생하였다.

원했던 동작:

```
Manual Control 종료 → 현재 위치에서 HOLD(LOITER)
```

실제 동작:

```
Manual Control 종료 → RTL(Return To Launch)
```

### 원인 분석

**원인 1. Manual Control 패킷을 너무 빨리 중단**

초기 구현에서는 수동 조종을 종료하면서 `StopManualControl()`을 먼저 호출하였다.

```
StopManualControl() 호출
  ↓
Manual Control 패킷 송신 중단
  ↓
PX4가 RC 입력이 사라진 것으로 판단
  ↓
Failsafe (RC Loss)
  ↓
RTL
```

PX4는 지속적으로 수신되던 `MANUAL_CONTROL` 메시지가 갑자기 중단되면 조종 입력이 상실(RC Loss)되었다고 판단하고, Failsafe 동작에 따라 RTL 모드로 전환하였다.

**원인 2. HOLD 모드 적용 전에 Manual Control 종료**

HOLD 모드 전환 요청만 보낸 뒤, 실제로 PX4가 HOLD 상태로 변경되기 전에 Manual Control을 종료한 것도 문제였다.

```
HOLD 요청
  ↓
아직 PX4는 Manual 상태
  ↓
Manual Control 종료
  ↓
RC Loss 발생
  ↓
RTL
```

GCS에서는 HOLD 명령을 보냈다고 생각했지만, PX4 내부에서는 아직 HOLD 모드로 변경되지 않은 상태였기 때문에 RC 입력이 사라진 것으로 판단하였다.

### 해결 방법

수동 조종 종료 순서를 다음과 같이 변경하였다.

```
① Joystick을 중립값으로 유지
  ↓
② HOLD(LOITER) 모드 요청
  ↓
③ PX4가 실제 HOLD 상태인지 확인
  ↓
④ HOLD 전환이 확인될 때까지 중립 Manual Control 계속 송신
  ↓
⑤ HOLD 확인 후 Manual Control 송신 종료
```

구체적으로는 다음과 같이 구현하였다.

- 조이스틱 값을 `(0, 0, 500, 0)`으로 설정하여 기체가 움직이지 않도록 중립 입력을 유지한다.
- `SetHoldModeAsync()`를 호출하여 PX4에 HOLD 모드 전환을 요청한다.
- 100ms 간격으로 최대 5초 동안 현재 PX4 비행 모드를 확인한다.
- HOLD 모드로 전환되는 동안에도 중립 Manual Control 패킷을 계속 전송하여 RC Loss가 발생하지 않도록 한다.
- PX4가 실제 HOLD 상태가 된 것이 확인되면 `StopManualControl()`을 호출하여 Manual Control 송신을 종료한다.

### 핵심 개선 사항

기존:

```
Manual 종료 → RC Loss → RTL
```

개선 후:

```
Joystick 중립 유지 → HOLD 요청 → HOLD 상태 확인 → Manual 종료
```

PX4가 Manual 입력 상실로 오인하지 않도록 순서를 보장하여, 수동 조종 종료 후에도 기체가 현재 위치에서 안정적으로 호버링하도록 개선하였다.

### 개선 결과

- 수동 조종 종료 후 RTL로 복귀하는 문제가 해결되었다.
- PX4가 RC Loss를 발생시키지 않고 현재 위치에서 안정적으로 HOLD 상태를 유지하였다.
- Manual Control과 AUTO HOLD 간의 전환이 자연스럽고 안정적으로 이루어졌다.

---

## 2. 드론 연결이 끊어진 후 자동으로 재연결되지 않는 문제

### 문제

GCS 실행 후 PX4와 정상적으로 연결되면 상태가 Connected로 표시되었다. 그러나 프로그램 실행 중 일시적인 통신 장애나 PX4 재시작 등으로 연결이 끊어지면 Disconnected 상태로 변경되었고, 이후 PX4가 다시 정상적으로 동작하더라도 GCS에서는 자동으로 재연결되지 않는 문제가 발생하였다.

결과적으로 드론과 실제 통신은 다시 가능해졌더라도 GCS에서는 장치를 다시 인식하지 못했으며, 프로그램을 종료한 뒤 다시 실행해야만 Connected 상태로 복구되었다.

### 원인 분석

**원인 1. Device Explorer의 장치 Timeout이 너무 짧음**

초기에는 Device Explorer 설정이 다음과 같이 매우 짧게 설정되어 있었다.

```csharp
DeviceTimeoutMs = 1000;
```

Device Explorer는 일정 시간 동안 Heartbeat를 수신하지 못하면 해당 장치를 목록에서 제거한다. 통신이 잠시 지연되거나 Gazebo·PX4의 부하로 Heartbeat가 1초 이상 늦게 도착하는 경우에도 장치를 제거하면서 Disconnected 상태로 변경되었다.

**원인 2. 장치 탐색(Device Discovery)이 최초 1회만 수행됨**

가장 큰 문제는 Device Explorer가 프로그램 시작 시 장치를 한 번만 탐색하도록 구성되어 있었다는 점이다.

```
GCS 실행 → Device Explorer 장치 탐색 → PX4 발견 → Connected
```

이후:

```
통신 끊김 → 장치 제거 → PX4 재연결 → 장치 재탐색 없음 → Disconnected 유지
```

Device Explorer가 장치를 제거한 이후에는 다시 탐색하지 않았기 때문에, PX4가 다시 Heartbeat를 송신해도 새로운 장치로 등록되지 않았다. 즉, GCS를 재실행해야만 Device Explorer가 다시 생성되면서 장치를 발견할 수 있었다.

**원인 3. GCS Heartbeat 미전송**

초기에는 GCS가 PX4의 Heartbeat만 수신하고 자신의 Heartbeat는 주기적으로 송신하지 않았다. 이로 인해 PX4 입장에서는 Ground Control Station의 상태를 지속적으로 확인하기 어려웠고, 연결 안정성에도 영향을 줄 수 있었다.

### 해결 방법

**① DeviceTimeout 증가**

장치 제거가 너무 쉽게 발생하지 않도록 Timeout을 다음과 같이 변경하였다.

```csharp
builder.SetConfig(new ClientDeviceBrowserConfig
{
    DeviceTimeoutMs = 30000,
    DeviceCheckIntervalMs = 1000
});
```

일시적인 통신 지연은 연결 해제로 판단하지 않도록 개선하였다.

**② GCS Heartbeat 주기적 송신**

GCS에서도 1초마다 Heartbeat를 송신하도록 구현하여 PX4와의 연결을 지속적으로 유지하였다.

```
GCS → Heartbeat → PX4
```

**③ 장치 탐색 로직 개선**

Device Explorer가 장치를 한 번만 탐색하는 구조를 개선하여, 연결이 끊어진 이후에도 지속적으로 장치 상태를 확인하고 새로운 Heartbeat를 수신하면 다시 장치를 인식할 수 있도록 탐색 로직을 수정하였다.

```
Disconnected → Heartbeat 재수신 → Device Explorer → 장치 재등록 → Connected
```

### 개선 결과

- 일시적인 통신 지연으로 Connected 상태가 쉽게 해제되지 않았다.
- PX4가 재시작되거나 네트워크가 일시적으로 끊겨도 장치를 다시 인식할 수 있게 되었다.
- GCS를 종료하고 다시 실행하지 않아도 Connected 상태로 자동 복구되었다.
- 장시간 운용 시 연결 안정성이 크게 향상되었다.

---

## 3. 텔레메트리 데이터 수신으로 인한 UI 성능 저하 문제

### 문제

GCS는 MAVLink를 통해 PX4로부터 드론의 텔레메트리 데이터를 실시간으로 수신한다. 수신되는 데이터는 다음과 같다.

- GPS(위도, 경도)
- Roll / Pitch / Yaw
- Altitude
- Speed
- Battery
- Flight Mode

초기 구현에서는 텔레메트리 메시지를 하나 수신할 때마다 UI를 즉시 갱신하도록 구현하였다.

```
Telemetry 수신 → ViewModel Property 변경 → PropertyChanged 발생 → UI 즉시 갱신
```

그러나 PX4는 초당 수십 개 이상의 MAVLink 메시지를 전송하므로 UI가 매우 빈번하게 다시 그려졌다. 그 결과 다음과 같은 문제가 발생하였다.

- 지도(Map) 이동이 끊기는 현상
- HUD가 깜빡이는 현상
- 영상 Overlay 갱신 지연
- CPU 사용률 증가
- 전체 GCS 화면이 버벅이는 현상

### 원인 분석

문제의 원인은 수신 이벤트마다 UI를 즉시 갱신하는 구조였다. 텔레메트리 메시지가 들어올 때마다

```
GPS 수신 → PropertyChanged → UI 갱신
Attitude 수신 → PropertyChanged → UI 갱신
Battery 수신 → PropertyChanged → UI 갱신
```

와 같이 모든 메시지마다 Dispatcher와 PropertyChanged가 반복적으로 호출되었다. 즉,

```
MAVLink → GPS → UI → Attitude → UI → Battery → UI ...
```

처럼 UI Thread가 계속 렌더링 작업을 수행하면서 화면 성능이 저하되었다.

### 해결 방법

UI를 즉시 갱신하는 대신 데이터 처리와 화면 갱신을 분리하였다.

먼저 MAVLink에서 수신한 텔레메트리 데이터를 Raw 변수에 저장하도록 변경하였다.

```
Telemetry 수신 → Raw 데이터 저장 (Raw Latitude, Raw Longitude, Raw Roll, Raw Pitch, Raw Yaw, Raw Altitude ...)
```

이후 별도의 UI 갱신 주기(Timer)를 사용하여 일정 시간마다 최신 데이터를 한 번에 화면에 반영하도록 변경하였다.

```
Telemetry 수신 → Raw 데이터 저장 → UI Timer(주기적 실행) → UI 일괄 갱신
```

기존에는 `수신 → 즉시 UI` 였다면, 개선 후에는 `수신 → Raw 데이터 저장 → 주기적인 UI 갱신` 구조로 변경하였다.

### 개선 결과

- UI Thread의 부하가 크게 감소하였다.
- 지도와 HUD의 움직임이 부드러워졌다.
- PropertyChanged 호출 횟수가 감소하여 렌더링 성능이 향상되었다.
- CPU 사용률이 감소하였다.
- 장시간 운용 시에도 GCS가 안정적으로 동작하였다.

---

## 4. RTP 영상과 YOLO 메타데이터의 시간 동기화 문제

### 문제

GCS에서는 Ubuntu로부터 다음 두 종류의 데이터를 동시에 수신하였다.

- RTP를 통한 실시간 카메라 영상
- UDP(JSON)를 통한 YOLO 객체 메타데이터

초기에는 YOLO 메타데이터를 수신하는 즉시 Bounding Box를 화면에 출력하였다. 그러나 실제 실행 결과 Bounding Box가 객체보다 약간 앞서거나 뒤처지는 현상이 발생하였으며, 드론이나 객체가 이동할수록 위치 오차가 더욱 커졌다.

### 원인 분석

원인은 영상과 YOLO 메타데이터의 전송 및 처리 속도가 서로 달랐기 때문이다.

카메라 영상 경로:

```
Gazebo → ROS2 → RTP Encoding → Network → LibVLC Buffer → GCS Video
```

YOLO 메타데이터 경로:

```
Gazebo → ROS2 → YOLO 추론 → UDP(JSON) → GCS Overlay
```

RTP 영상은 LibVLC의 디코딩 및 버퍼링 과정 때문에 YOLO 메타데이터보다 약간 늦게 화면에 출력되었다. 하지만 초기에는 YOLO 메타데이터를 수신 즉시 Overlay에 그렸기 때문에,

```
YOLO(Frame 100) → 즉시 출력
영상(Frame 100) → 조금 늦게 출력
```

과 같은 순서가 발생하였다. 결과적으로 Bounding Box는 실제 영상보다 먼저 출력되어 객체 위치와 맞지 않는 현상이 발생하였다.

### 해결 방법

영상 지연 시간에 맞추기 위해 YOLO 메타데이터를 Queue에 저장한 뒤 일정 시간 지연시켜 출력하도록 변경하였다.

Overlay Timer에서는 먼저 최신 메타데이터를 Queue에 저장한다.

```csharp
EnqueueOverlayFrame();
```

이후 영상 출력 지연에 맞는 시점의 Bounding Box를 선택하여 화면에 출력한다.

```csharp
UpdateOverlayBounds();
RedrawBboxes();
```

기존에는 `YOLO 수신 → 즉시 Bounding Box 출력 → 영상 출력` 이었다면, 개선 후에는 `YOLO 수신 → Queue 저장 → 영상 출력 시점까지 대기 → Bounding Box 출력` 구조로 변경하였다.

이를 통해 영상과 Bounding Box가 동일한 프레임 기준으로 표시되도록 시간을 동기화하였다.

### 개선 결과

- Bounding Box가 객체 위치와 정확하게 일치하였다.
- 드론 이동 중에도 Overlay가 자연스럽게 따라오도록 개선되었다.
- 객체 선택 정확도가 향상되었다.
- 영상과 YOLO 메타데이터의 출력 시점을 동기화하여 실시간 객체 추적의 신뢰성을 높였다.
