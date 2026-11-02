# 화면 번역기 (ScreenTranslator)

화면의 외국어 글자를 OCR로 읽어 **한국어로 번역한 뒤, 원문 위에 그대로 덮어서** 보여주는 Windows 트레이 프로그램이에요.

## 사용법
| 단축키 | 동작 |
|---|---|
| Ctrl+Alt+T | 영역을 드래그해서 한 번 번역 (번역 화면을 클릭하면 닫혀요) |
| Ctrl+Alt+L | 영역을 정하고 **실시간 번역** 시작/중지 (화면이 바뀔 때만 다시 번역) |
| Ctrl+Alt+F | 지금 마우스가 있는 모니터 전체를 번역 |
| Ctrl+Alt+X | 번역 지우기 / 실시간 중지 |

트레이 아이콘 우클릭으로도 모두 실행할 수 있어요.

## 설치 (처음 한 번)
1. `ScreenTranslator.cs`, `build.bat`, `version.txt` 를 한 폴더에 둬요.
2. `build.bat` 더블클릭 → `ScreenTranslator.exe` 생성 (Windows 내장 컴파일러라 설치할 게 없어요).
3. exe 실행 → 트레이 우클릭 → "Windows 시작 시 자동 실행" 체크.

## 자동 업데이트 (추가 다운로드 불필요)
- 6시간마다(그리고 실행 30초 뒤) GitHub의 `version.txt` 를 확인해요.
- 숫자가 더 크면 `ScreenTranslator.cs` 를 받아 **이 PC에서 컴파일 → exe 교체 → 자동 재시작**해요.
- 새 버전 올리는 법: `.cs` 의 `AppVersion` 숫자와 `version.txt` 숫자를 **같은 값으로** 올려서 GitHub `main` 브랜치에 push.
- 빌드가 실패하면 기존 버전을 그대로 쓰고, 그 버전은 건너뛰어요 (`%APPDATA%\ScreenTranslator\update.log` 에 기록).
- 기본 주소는 `https://raw.githubusercontent.com/SnoWhite75/screen-translator/main/` 이에요. 본인 저장소를 만들어 **트레이 > 업데이트 > 업데이트 주소 설정**에서 바꿔 주세요.

## 요구사항
- Windows 10/11 (내장 OCR 사용). 번역할 언어의 OCR 언어팩이 필요해요: 설정 > 시간 및 언어 > 언어 및 지역 > 언어 추가 (영어/일본어/중국어 등)
- 인터넷 연결 (번역에 Google 번역 웹 엔드포인트 사용)

## 참고
- 한국어로 판별된 글자는 번역하지 않아요.
- 번역 오버레이는 화면 캡처에서 제외돼서 실시간 모드에서 번역문을 다시 읽는 일이 없어요.
- 비공식 무료 번역 엔드포인트라 요청이 몰리면 잠시 막힐 수 있어요. 안정적으로 쓰려면 나중에 DeepL/Google Cloud 키 방식으로 바꿀 수 있어요.
