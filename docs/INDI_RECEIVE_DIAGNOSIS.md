# 실제 Windows ReceiveData 미수신 분석

## 2026-11-13 2.2.4 첫 계좌 조회의 GI004

사용자11:22 화면에서는 종목 요청5374의 정상 수신 뒤 첫 `SABA609Q1` 요청이0/GI004로 실패했다. 별도로 주문 정의 검사에 `UnsupportedFormat`이 표시됐다. 609 입력과 조회 코디네이터의 원래 요청 절차는2.2.3과 같으며 두 오류 사이의 인과관계는 확인되지 않았다. `GI004` 문구만으로 XTR 파일 자체의 파싱 실패라고 확정하지 않는다. 공식 OCX의 IPC 수신 측0 이하 반환도 이 코드로 이어질 수 있다.

2.2.5는 초기 계좌 조회 때 주문 OCX 생성·주문 명세 읽기를 하지 않고 실제 주문 준비 전에 수행한다. 이후 새 사전조회로 금융 근거를 수집하고 초기화 중 중단·폐기·계좌 변경을 재검사한다. 읽기 전용 계좌 조회가 명시적인 정수0/GI004로 미접수된 경우만1초 후 최대1회 전부 다시 설정한다. 대기 중 새 콜백은 해당 응답으로 확정하지 않으며 실제 새 요청번호·시작시각·정상 콜백을 대조한다. 취소·중단·폐기·2차 실패는 중단하고 금융 주문을 재전송하지 않는다. 이 격리·재시도는 실제 사용자 GI004의 원인 해결을 입증한 결과가 아니다.

사용자 설치의609 정의는3,844바이트이며 확보된 구형 배포본은2,981바이트다. 이는 참조본과 다르다는 증거이며 버전 교체 사이에 설치 파일이 갱신됐다는 증거는 아니다. `조회 정의 내보내기`가 저장하는 XML의 정적609/101/102/231 XTR와 Helper를 확인해야 현재 설치의 실제 형식과 입력 항목을 분석할 수 있다.2.2.10부터231 정의도 포함한다. 현재 형식을 추정해 실주문을 허용하지 않는다.

확인일: 2026-11-12. 사용자 화면의 시각은 프로그램이 표시하는 한국시간(KST)이다.

## 확인한 증거와 한계

- 사용자 Windows 화면: `18:23:21 COM outgoing IID=f52e833f-1d17-4184-be0d-6b9f59de7dac`, `ReceiveData DISPID=1`, `ReceiveSysMsg DISPID=3`, `handlers attached on STA`가 출력됐다.
- 이어서 `REQUEST stock_mst rid=0`가 출력됐고 `18:23:41`에 20초 타임아웃이 발생했다. 이 실행에서 `ReceiveData` 수신은 확인되지 않았다. 이벤트 연결 API가 예외 없이 반환한 사실을 실제 이벤트 전달 성공으로 취급하면 안 된다.
- 사용자는 같은 PC의 기존 신한 Indi/Helper가 정상 동작한다고 확인했다. 따라서 로그인 누락을 원인으로 단정하거나, 같은 환경 설정을 다시 하도록 요구할 근거가 없다. 애플리케이션의 ActiveX 사이트·활성화·이벤트 수명과 요청 결과 진단을 먼저 확인한다.
- 기존 Windows CI 성공은 빌드, x86 실행, 가짜 전송과 모의 원장의 검증 결과다. 설치된 Indi OCX에서 실제 데이터 수신이 성공했다는 결과가 아니다.

## 공개 생성 래퍼와 비교

[Stock-Check의 Interop 래퍼](https://github.com/JunyoungChoi/Stock-Check/blob/abd23864a3c938e5f2f78263d40204f7f34d5870/Stock%20Check/lib/Interop.GIEXPERTCONTROLLib.dll)와 [AxInterop 래퍼](https://github.com/JunyoungChoi/Stock-Check/blob/abd23864a3c938e5f2f78263d40204f7f34d5870/Stock%20Check/lib/AxInterop.GIEXPERTCONTROLLib.dll)를 코드 실행 없이 메타데이터/IL로 읽었다. 공개 저장소의 과거 생성물이며 현재 신한 공식 배포물로 인증한 것은 아니다. 다만 outgoing IID는 위 사용자 화면과 정확히 일치한다.

| 멤버 | 생성 래퍼의 정의 |
| --- | --- |
| outgoing interface | `F52E833F-1D17-4184-BE0D-6B9F59DE7DAC` |
| `ReceiveData` / `ReceiveSysMsg` | 각 `void(Int16)`, DISPID 1 / 3 |
| `RequestData` | `Int16()`, DISPID 5 |
| `GetErrorState` / `GetCommState` | 각 `Int16()`, DISPID 14 / 17 |
| `GetErrorCode` / `GetErrorMessage` | 각 `Object()`, DISPID 15 / 16 |

생성 AxHost 래퍼는 `AttachInterfaces()`에서 `GetOcx()`를 확보하고, `CreateSink()`에서 event multicaster와 `AxHost.ConnectionPointCookie`를 생성하며, `DetachSink()`에서 연결을 해제한다. 일반 생성자에서 컨트롤 생성 후 나중에 연결하는 방식과 비교해야 할 수명 단계다. `RequestData()` 래퍼 자체는 반환값을 그대로 전달하며 성공/실패 범위를 설명하지 않는다.

[WPF 예제의 Designer](https://github.com/yu4321/ShinhanIndi_Example_WPFVersion/blob/323a6b6203051b3605d0abe1fd4de2c0f7b6740a/GiexpertLib/GiExpertAx.Designer.cs)는 `BeginInit`, 저장된 `OcxState`, `Controls.Add`, `EndInit` 순서로 컨트롤을 구성한다. [GiExpertStarter](https://github.com/yu4321/ShinhanIndi_Example_WPFVersion/blob/323a6b6203051b3605d0abe1fd4de2c0f7b6740a/GiexpertLib/GiExpertStarter.cs)는 STA 스레드와 메시지 루프를 사용한다. 이 예제의 과거 스레드 종료 코드를 그대로 채택하지는 않는다.

## 반환값과 오류 진단

[공개 VB 호출부](https://github.com/exxocism/shinhan-stock-trade/blob/38be117affffc572a55b94fb7be2e83b3f9f4298/Plugin_code/Shinhan_indi_VBA_powerkit/Class1.vb)는 요청 반환값 `<=0`를 송신 오류로 처리하고 `GetErrorState`와 `GetErrorMessage`를 조회한다. [같은 저장소의 수신부](https://github.com/exxocism/shinhan-stock-trade/blob/38be117affffc572a55b94fb7be2e83b3f9f4298/Plugin_code/Shinhan_indi_VBA_powerkit/Form1.vb)는 `GetErrorState()>0`를 오류로 처리한다. 이것은 해당 작성자의 구현 근거이며 공식 반환값 규약을 대신하지 않는다.

현재 확보한 자료만으로 `rid=0` 자체를 실패로 확정하지 않는다. 설치된 typelib에 정의와 인자형이 확인된 경우에만 인자 없는 `GetCommState`, `GetErrorState`, `GetErrorCode`, `GetErrorMessage`를 읽고, 요청 직후 및 타임아웃의 진단을 구분한다. `GetCommState=0`을 임의로 접속 끊김으로 해석하지 않는다. 오류가 확인되지 않은 0번 요청의 정상 콜백을 버려서도 안 된다. 요청 필드·계좌·비밀번호와 공급자 오류 원문은 진단 로그에 무방비로 저장하지 않는다.

## 공식 자료 접근과 검증 범위

[WPF 예제 README](https://github.com/yu4321/ShinhanIndi_Example_WPFVersion/blob/323a6b6203051b3605d0abe1fd4de2c0f7b6740a/README.md)는 공식 C# WinForms 예제를 포팅했다고 설명하고 `http://file.shinhaninvest.com/filedoc/indi/sample/indi_csharp.zip`를 출처로 든다. 이번 조사에서 해당 경로의 HTTPS 요청과 `file.shinhansec.com`의 같은 경로는 모두 HTTP 503이어서 공식 ZIP 원본을 확보하지 못했다.

네이티브 COM fixture를 통한 테스트는 Windows COM 연결점·IDispatch·구독 보관/해제를 검증한다. 신한 OCX의 AxHost 활성화와 서버를 사용한 실제 수신 검증을 대신하지 않는다. 새로운 EXE에서도 사용자 Windows Indi의 `ReceiveData`와 응답 행수가 확인되기 전에는 수신 문제 해결로 보고하지 않는다. 실주문은 계속 비활성화한다.

## 1.2.0 실제 실행에서 새로 확인된 GI004

사용자가 보낸 18:50:50 KST 로그에서 `CommState=0`, `GetErrorState=2`, `RequestData` 반환형 `Int16`, 요청번호0, 오류코드 `GI004`, 메시지 `조회 데이터 파싱 오류 입니다`가 확인됐다. 이번에는 `ReceiveData`를 기다리기 전에 OCX가 명시적인 요청 오류를 반환했다. 이벤트 구독만 바꾸어 해결됐다고 볼 근거가 없다.

1.3.0은 공개 C# 호출부와 비교해 빠져 있던 OCX별 `StartIndi("", "", "", 설치된 starter 경로)`를 실행한다. 기존 Indi/Helper가 정상이어도 새로 생성한 OCX에서 이 호출을 생략한 차이는 남아 있다. 내부 파서 리소스가 어떻게 초기화되는지는 확인하지 못했으므로 GI004의 확정 원인이라고 주장하지 않는다.

또한 생성 Interop과 같은 읽기 전용 dispinterface로 호출을 바꿔 VARIANT 문자열, Int16 행/필드 인덱스, 기본 COM LCID를 사용한다. 최초 조회는 초기화 호출 스택에서 분리해 UI 메시지 루프에 넘기며, 실제 설정된 쿼리 이름을 GetQueryName으로 읽어 대조한다. 신한 OCX의 정상적인 숨김 속성이나 작은 OcxState를 오류 원인으로 단정해 복제하지 않는다.
