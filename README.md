# Shinhan Native Trader 1.0 – 신한 인디 ActiveX 새 구현

## 먼저 읽어주세요

이 프로그램은 이전 v0.3.x Python/PyQt/PyInstaller 패치가 아닙니다. **Windows C# .NET Framework 4.8 / x86 / WinForms AxHost / COM connection-point** 로 처음부터 구현한 별도 프로젝트입니다.

**실계좌로 매수·매도 주문을 전송하는 기능은 이 1.0 빌드에 없습니다.** 이전 v0.3.x에서 접수·체결 추적이 검증되지 않았으므로, 주식 실주문 경로를 잠갔습니다. 이 제품을 완성된 실전 자동매매 프로그램이라고 부르지 않습니다.

이 빌드의 목적은 한 번의 실행파일에서 ① 신한 종목조회 ② 실제 일봉 ③ 실제 현재가 ④ 실제 잔고 조회 ⑤ 실제 가격 기반 전략 후보 선별 및 가상매수/매도까지 검증하는 것입니다. **신한i indi/Indi Helper가 정상인 Windows PC에서 실제 이벤트 수신은 아직 테스트하지 못했습니다.** 현재 GitHub 빌드도 실행 검증되지 않았습니다.

## 왜 바꾸었나

기존 진단 로그에서 `RequestData() -> rid=0` 호출까지만 진행하고 `ReceiveData`가 타임아웃되었습니다. 사용자 PC의 Indi Helper에서는 같은 `stock_mst`가 정상 조회됐습니다. Python QAxWidget 대신 **Windows 네이티브 ActiveX 호스트 AxHost**와 .NET Framework COM 이벤트 수신 연결점(ComEventsHelper)을 사용하는 구조입니다. 이 방식의 실제 동작도 PC에서 확인해야 합니다.

## 설치 / EXE 생성 (사용자 PC 파이썬 설치 필요 없음)

1. 이 ZIP을 압축 해제합니다.
2. GitHub에서 새 **Private** 저장소를 만들거나 기존 `shinhan-autotrader` 저장소에 **새 폴더가 아니라 루트의 소스파일** `Program.cs`, `IndiCom.cs`, `Strategy.cs`, `SelfTests.cs`, `ShinhanNativeTrader.csproj`와 `.github/workflows/build-win32.yml`을 업로드합니다. 기존 Python `app.py`가 있어도 새 C# 빌드에는 사용하지 않습니다.
3. GitHub **Actions → Build Shinhan Native Win32 → Run workflow**를 실행합니다. 기존 `Build Shinhan AutoTrader Win32` 액션과 **다른 워크플로**입니다.
4. 성공하면 **Artifacts → ShinhanNativeTrader-Win32**에서 EXE를 다운로드합니다.
5. Windows PC에 Indi 설치와 로그인 세션이 있는 상태에서 `ShinhanNativeTrader.exe`를 실행합니다. Windows .NET Framework 4.8 실행환경이 필요하며 Python은 필요 없습니다.

## 버튼 설명

- **① 신한 종목조회 연결**: `stock_mst` 요청. 성공하면 실제 종목 건수 표시. 연결이 실패하면 여기서 멈춥니다.
- **② 일봉 분석**: 6자리 종목코드의 `TR_SCHART` 일봉 조회 후 신호 계산.
- **현재가 조회**: 해당 코드의 `SC` 조회.
- **③ 전체종목 순차스캔**: 전종목 일봉을 950ms 간격으로 순차 조회해 전략 후보 탐색. 전략은 직전 5/20일 평균 추세, 20일 고점 돌파, 거래량 조건.
- **④ 실제 잔고 조회**: `SABA200QB` 조회 전용. 계좌번호와 비밀번호는 화면에만 입력하며 저장하지 않습니다. **계좌잔고와 거래 기록을 절대로 가상잔고에 합치지 않습니다.**
- **즉시 스캔 중단**: 추후 요청 중단 (진행 중인 신한 조회 취소 명령은 보내지 않음).

로그는 `%LOCALAPPDATA%\ShinhanNativeTrader\diagnostics.log`, 가상거래 기록은 `%LOCALAPPDATA%\ShinhanNativeTrader\paper_trades.csv`에 저장됩니다. 비밀번호나 계좌번호는 로그하지 않습니다.

## 실제 매매 이전에 해결해야 할 기능

- C# 프로그램에서 실제 `ReceiveData` 수신 검증
- 주문가능현금 조회의 **현재 신한 TR 및 출력 필드** 공식 사양 확보. 단순 예수금이나 사용자가 입력한 자금을 주문가능현금으로 취급하면 안 됩니다.
- 계좌별 체결/미체결 실시간 이벤트 `AA, AB, AD, AE` 검증과 주문번호별 체결·취소·정정 및 재시작 복구
- 주문 금액을 주문가능금액에 제한하고, 미수/신용 전면 차단
- 실제 자동매도 타이밍·슬리피지·전략 유효성 검증과 장애 복구

신한 공식 FAQ는 최초 잔고/주문체결 TR 이후 **실시간 주문체결 AA/AB 및 잔고 AD/AE**를 이용하도록 안내합니다. `ReceiveData`가 성공한 것만으로 실거래 자동매매가 완성되는 것은 아닙니다.

## 자료

- 신한투자증권 신한i indi 공식 서비스: https://www.shinhansec.com/siw/customer-center/channel/channel_homeTrading-indi/contents.do
- 신한i indi 공식 FAQ: https://www.shinhansec.com/siw/customer-center/channel/channel_homeTrading-indi_faq/contents.do
- stock_mst TR 사용법: https://wikidocs.net/22664
- TR_SCHART 일봉 필드: https://wikidocs.net/78514
- SABA200QB 잔고 필드: https://wikidocs.net/22715

## 전략과 돈의 위험

이 전략은 예시 규칙이며, 수익성·안정성은 백테스트나 실거래에서 입증되지 않았습니다. 국내주식은 시세 급변·미체결·거래중단 등이 발생할 수 있습니다. 신한 실계좌에서 주문가능액 조회와 체결확인이 검증되기 전에는 실제 주문 기능을 제공하지 않습니다.
