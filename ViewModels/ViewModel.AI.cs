using GCS_Project.Data;
using GCS_Project.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace GCS_Project.ViewModels
{
    public partial class ViewModel
    {
        #region [ AI 로직(OpenAI) ]

        private readonly string? _openAiApiKey =
            Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        // AI 명령 전송 실행
        private async Task ExecuteSendAICommand()
        {
            if (string.IsNullOrWhiteSpace(UserInputCommand)) return;

            string userMsg = UserInputCommand;
            UserInputCommand = string.Empty;

            AppendLog($"[{DateTime.Now:HH:mm:ss}] USER >> {userMsg}");

            // [DB 저장] 사용자의 명령 기록
            await Task.Run(() => {
                using (var db = new AppDbContext())
                {
                    db.SystemLogs.Add(new SystemLog { Timestamp = DateTime.Now, Sender = "USER", Message = userMsg });
                    db.SaveChanges();
                }
            });

            // 1. AI에게 JSON 형태의 응답을 받아옵니다.
            string aiJsonResponse = await GetOpenAIResponse(userMsg);

            // [추가된 방어막] 통신 실패 메시지가 오면 JSON 파싱 안 하고 바로 출력!
            if (aiJsonResponse.StartsWith("[통신 실패]") || aiJsonResponse.StartsWith("[오류 발생]"))
            {
                AppendLog(aiJsonResponse);
                return;
            }

            try
            {
                string cleanJson = aiJsonResponse
                    .Replace("```json", "")
                    .Replace("```", "")
                    .Trim();

                AiCommandResponse? aiCommand = JsonConvert.DeserializeObject<AiCommandResponse>(cleanJson);

                if (aiCommand == null)
                {
                    AppendLog("[시스템] AI 명령을 해독할 수 없습니다.");
                    return;
                }

                AppendLog($"[{DateTime.Now:HH:mm:ss}] AI >> {aiCommand.Message}");

                switch (aiCommand.Action?.ToUpperInvariant())
                {
                    case "ARM":
                        ExecuteArm();
                        break;

                    case "DISARM":
                        ExecuteDisarm();
                        break;

                    case "TAKEOFF":
                        {
                            double altitude = aiCommand.Value > 0 ? aiCommand.Value : 5;

                            Drone.TargetAltitude = altitude;

                            AppendLog($"[명령] 이륙 목표 고도 설정: {Drone.TargetAltitude:F1}m");

                            ExecuteTakeoff();
                            break;
                        }

                    case "LAND":
                        ExecuteLanding();
                        break;

                    case "RTL":
                        ExecuteRtl();
                        break;

                    case "STRIKE":
                        if (SelectedTarget != null)
                            ExecuteFollow();
                        else
                            AppendLog("[시스템 오류] 요격할 타겟이 선택되지 않았습니다.");
                        break;

                    case "ASCEND":
                        if (aiCommand.Value > 0)
                        {
                            Drone.TargetAltitude = aiCommand.Value;
                            await _mavlinkService.ChangeTargetAltitude((float)Drone.TargetAltitude);
                            AppendLog($"[명령] 목표 고도를 {Drone.TargetAltitude:F1}m로 상승시킵니다.");
                        }
                        break;

                    case "DESCEND":
                        if (aiCommand.Value > 0)
                        {
                            Drone.TargetAltitude = aiCommand.Value;

                            if (Drone.TargetAltitude < 1)
                                Drone.TargetAltitude = 1;

                            await _mavlinkService.ChangeTargetAltitude((float)Drone.TargetAltitude);
                            AppendLog($"[명령] 목표 고도를 {Drone.TargetAltitude:F1}m로 하강시킵니다.");
                        }
                        break;

                    case "SCAN":
                        await ExecuteScan(aiCommand.Value);
                        break;
                    case "STOP_SCAN":
                        ExecuteStopScan();
                        break;

                    case "NONE":
                    default:
                        AppendLog("[AI] 실행 가능한 명령이 아닙니다.");
                        break;
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[시스템] AI 명령 해석 실패. 다시 명령해 주세요. (오류: {ex.Message})");
            }
        }

        private async Task<string> GetOpenAIResponse(string input)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_openAiApiKey))
                {
                    return "[오류 발생] OPENAI_API_KEY 환경변수가 설정되지 않았습니다.";
                }

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", _openAiApiKey);

                    var url = "https://api.openai.com/v1/responses";

                    var requestBody = new
                    {
                        model = "gpt-4.1-mini",
                        input = $"사용자 명령: {input}",
                        instructions =
@"너는 자율 드론 GCS 명령 해석 AI다.
사용자의 자연어 명령을 분석해서 반드시 하나의 JSON 객체로만 판단해라.

Action은 반드시 다음 중 하나만 사용:
ARM, DISARM, TAKEOFF, LAND, RTL, STRIKE, ASCEND, DESCEND, SCAN, STOP_SCAN, NONE

판단 규칙:
- 시동 걸기, arm, 모터 켜기, 비행 준비 = ARM
- 시동 끄기, disarm, 모터 끄기 = DISARM
- 이륙 = TAKEOFF
- 착륙 = LAND
- 복귀, 귀환 = RTL
- 선택된 타겟 추적, 요격, 따라가기 = STRIKE
- 고도를 올려라, 상승해라 = ASCEND
- 고도를 낮춰라, 하강해라 = DESCEND
- 주변 탐지, 객체 탐지, 회전 탐색, 빙글빙글 돌며 탐색 = SCAN
- 탐색 멈춰, 회전 멈춰, 탐지 중단, 스캔 중지 = STOP_SCAN
- 의미가 모호하거나 실행 불가하면 = NONE

Value 규칙:
- TAKEOFF의 Value는 목표 이륙 고도(m)
- TAKEOFF의 Value가 0이면 기본 이륙 고도는 5m로 설정한다.
- ASCEND의 Value는 목표 고도(m)
- DESCEND의 Value도 목표 고도(m)
- DESCEND의 Value는 추가 하강량(m)
- SCAN의 Value는 회전 시간(초), 없으면 10
- STOP_SCAN의 Value는 0
- ARM, DISARM, LAND, RTL, STRIKE, NONE의 Value는 0
- 숫자가 명시되지 않으면 기본값 0

Message는 짧은 한국어 보고 문장으로 작성해라.
부가 설명, 코드블록, 마크다운은 출력하지 마라.",

                    text = new
                        {
                            format = new
                            {
                                type = "json_schema",
                                name = "drone_command",
                                schema = new
                                {
                                    type = "object",
                                    additionalProperties = false,
                                    properties = new
                                    {
                                        Action = new
                                        {
                                            type = "string",
                                            @enum = new[] { "ARM", "DISARM", "TAKEOFF", "LAND", "RTL", "STRIKE", "ASCEND", "DESCEND", "SCAN", "STOP_SCAN", "NONE" }
                                        },
                                        Value = new
                                        {
                                            type = "number"
                                        },
                                        Message = new
                                        {
                                            type = "string"
                                        }
                                    },
                                    required = new[] { "Action", "Value", "Message" }
                                }
                            }
                        }
                    };

                    var jsonPayload = JsonConvert.SerializeObject(requestBody);
                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    var response = await client.PostAsync(url, content);
                    var responseString = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        return $"[통신 실패] 코드: {response.StatusCode}\n상세 이유: {responseString}";
                    }

                    dynamic? json = JsonConvert.DeserializeObject(responseString);

                    if (json == null)
                    {
                        return "[오류 발생] OpenAI로부터 빈 응답이 도착했습니다.";
                    }

                    // Responses API에서는 output_text에 텍스트가 집계되어 내려올 수 있음
                    string text = json.output[0].content[0].text.ToString();

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return $"[오류 발생] 응답 본문에서 text를 찾지 못했습니다.\n원본: {responseString}";
                    }

                    return text.Trim();
                }
            }
            catch (Exception ex)
            {
                return $"[오류 발생] OpenAI 연결 불가: {ex.Message}";
            }
        }

        #endregion
    }
}