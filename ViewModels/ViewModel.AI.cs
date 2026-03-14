using GCS_Project.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace GCS_Project.ViewModels
{
    public partial class ViewModel
    {
        #region [ AI 로직(제미나이) ]
        private readonly string _geminiApiKey = "AIzaSyBX3h7Ftj6N88_Fw43ZSzACxRYwYvK2PkI";

        // 13. AI 명령 전송 실행
        private async Task ExecuteSendAICommand()
        {
            if (string.IsNullOrWhiteSpace(UserInputCommand)) return;

            string userMsg = UserInputCommand;
            UserInputCommand = string.Empty;

            ChatLog += $"\n[{DateTime.Now:HH:mm:ss}] USER >> {userMsg}";

            // 1. AI에게 JSON 형태의 응답을 받아옵니다.
            string aiJsonResponse = await GetGeminiResponse(userMsg);

            // ✨ [추가된 방어막] 통신 실패 메시지가 오면 JSON 파싱 안 하고 바로 출력!
            if (aiJsonResponse.StartsWith("[통신 실패]") || aiJsonResponse.StartsWith("[오류 발생]"))
            {
                ChatLog += $"\n{aiJsonResponse}";
                return;
            }

            try
            {
                // 1. 코드블록 마크다운(```json 등) 제거
                string cleanJson = aiJsonResponse.Replace("```json", "").Replace("```", "").Trim();

                AiCommandResponse? aiCommand = null;

                // 2. 스마트 파싱 로직 (배열로 오든 객체로 오든 다 처리!)
                if (cleanJson.StartsWith("["))
                {
                    // 제미나이가 [...] 배열 형태로 보냈을 경우: 리스트로 풀어서 첫 번째 것만 가져옴
                    var list = JsonConvert.DeserializeObject<List<AiCommandResponse>>(cleanJson);
                    aiCommand = list?.FirstOrDefault();
                }
                else
                {
                    // 제미나이가 {...} 단일 객체 형태로 보냈을 경우: 바로 변환
                    aiCommand = JsonConvert.DeserializeObject<AiCommandResponse>(cleanJson);
                }

                if (aiCommand == null)
                {
                    ChatLog += "\n[시스템] AI 명령을 해독할 수 없습니다.";
                    return;
                }

                // 3. AI의 친절한 대답은 로그에 출력
                ChatLog += $"\n[{DateTime.Now:HH:mm:ss}] AI >> {aiCommand.Message}";

                // 4. AI의 판단(Action)에 따라 MavlinkService로 명령을 넘깁니다!!
                switch (aiCommand.Action)
                {
                    case "TAKEOFF":
                        if (aiCommand.Value > 0) Drone.TargetAltitude = aiCommand.Value;
                        ExecuteTakeoff(); // 실제 이륙 함수 호출
                        break;
                    case "LAND":
                        ExecuteLanding();
                        break;
                    case "RTL":
                        ExecuteRtl();
                        break;
                    case "STRIKE":
                        if (SelectedTarget != null) ExecuteFollow();
                        else ChatLog += "\n[시스템 오류] 요격할 타겟이 선택되지 않았습니다.";
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

        // 13 - 1 제미나이 API 연동 함수 (현재는 시뮬레이션)
        private async Task<string> GetGeminiResponse(string input)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    // 1. 혹시 모를 숨겨진 공백이나 줄바꿈 완벽 제거
                    string cleanApiKey = _geminiApiKey.Trim();

                    // 2. 가장 확실한 최신 모델 주소(-latest 추가)로 변경! 철자 주의!
                    var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent?key={cleanApiKey}";

                    // AI에게 내리는 역할 부여 (프롬프트)
                    var requestBody = new
                    {
                        contents = new[]
                        {
                            new {
                                parts = new[] {
                                    new { text = $@"
                                            너는 자율 요격 드론 지휘 AI야. 사용자의 명령을 분석해서 반드시 아래 JSON 형식으로만 대답해. 부가 설명은 절대 하지마.
                                            형식: {{ ""Action"": ""TAKEOFF"", ""Value"": 0, ""Message"": ""사령관에게 보고할 짧은 대답"" }}
                                            규칙 1: Action의 값은 반드시 [TAKEOFF, LAND, RTL, STRIKE, NONE] 이 5개 단어 중 하나만 써야 해.
                                            규칙 2: 반드시 대괄호([])로 묶인 배열이 아닌, 중괄호({{}})로 묶인 단일 JSON 객체 형태로만 출력해.
                                            사용자 명령: {input}" }
                                }
                            }
                        }
                    };

                    var jsonPayload = JsonConvert.SerializeObject(requestBody);
                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    // 요청 보내기
                    var response = await client.PostAsync(url, content);
                    var responseString = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        // 복잡한 JSON 결과에서 AI의 답변만 쏙 빼오기
                        dynamic? json = JsonConvert.DeserializeObject(responseString);

                        if (json == null)
                            return "[시스템 오류] AI로부터 빈 응답이 도착했습니다.";
                        
                        string text = json.candidates[0].content.parts[0].text;
                        return text.Trim();
                    }
                    else
                    {
                        return $"[통신 실패] 코드: {response.StatusCode}\n상세 이유: {responseString}";
                    }
                }
            }
            catch (Exception ex)
            {
                return $"[오류 발생] AI 연결 불가: {ex.Message}";
            }
        }
        #endregion
    }
}
