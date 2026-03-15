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

            ChatLog += $"\n[{DateTime.Now:HH:mm:ss}] USER >> {userMsg}";

            string aiJsonResponse = await GetOpenAIResponse(userMsg);

            if (aiJsonResponse.StartsWith("[통신 실패]") || aiJsonResponse.StartsWith("[오류 발생]"))
            {
                ChatLog += $"\n{aiJsonResponse}";
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
                    ChatLog += "\n[시스템] AI 명령을 해독할 수 없습니다.";
                    return;
                }

                ChatLog += $"\n[{DateTime.Now:HH:mm:ss}] AI >> {aiCommand.Message}";

                switch (aiCommand.Action?.ToUpperInvariant())
                {
                    case "ARM":
                        ExecuteArm();
                        break;

                    case "DISARM":
                        ExecuteDisarm();
                        break;

                    case "TAKEOFF":
                        if (aiCommand.Value > 0)
                            Drone.TargetAltitude = aiCommand.Value;
                        ExecuteTakeoff();
                        break;

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
                            ChatLog += "\n[시스템 오류] 요격할 타겟이 선택되지 않았습니다.";
                        break;

                    case "NONE":
                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                ChatLog += $"\n[시스템] AI 명령 해석 실패. 다시 명령해 주세요. (오류: {ex.Message})";
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
                            사용자의 자연어 명령을 해석해서 아래 Action 중 하나로 분류한다.

                            Action은 반드시 다음 중 하나만 사용:
                            ARM, DISARM, TAKEOFF, LAND, RTL, STRIKE, NONE

                            규칙:
                            - 모터 시동 / arm / 비행 준비 → ARM
                            - 시동 해제 / 모터 끄기 → DISARM
                            - 이륙 명령 → TAKEOFF
                            - 착륙 명령 → LAND
                            - 복귀 명령 → RTL
                            - 선택된 타겟 추적 / 요격 → STRIKE
                            - 의미가 불명확하거나 실행 불가능 → NONE

                            추가 규칙:
                            - Message는 짧은 한국어 보고 문장
                            - Value는 숫자 (필요 없으면 0)
                            ",

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
                                            @enum = new[] { "Arm", "DISARM", "TAKEOFF", "LAND", "RTL", "STRIKE", "NONE" }
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