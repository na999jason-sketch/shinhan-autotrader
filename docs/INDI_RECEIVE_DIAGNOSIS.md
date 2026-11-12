# 실제 Windows ReceiveData 미수신 분석

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
