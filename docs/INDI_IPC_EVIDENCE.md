# 공식 Indi 배포 OCX의 GI004 통신 경로

확인일: 2026-11-12 KST. 배포 파일을 설치·실행하지 않고 정적으로 추출해 검사했다. 사용자 PC에 설치된 파일과 버전/해시가 같다는 확인은 아직 없다.

## 배포 파일과 출처

- 공식 배포 주소: `http://file.shinhaninvest.com/filedoc/download/IndiSetup.exe`
- 내려받은 파일: 28,757,720 bytes, SHA256 `5f18d7ca4ddc1f44835cf64ffa214844b8525cf0e37323351c99c661fadc318f`
- 내장 서명 인증서의 주체는 `Shinhan Securities Co.,Ltd.`, 타임스탬프는 2023-09-13이다. 파일의 Authenticode payload digest는 내장 digest와 일치했다. 이 환경의 타임스탬프/CRL 검증은 완료하지 못했으므로 완전한 온라인 서명 검증 성공으로 표시하지 않는다.
- InstallShield 자료를 실행 없이 추출했다. 신한 바이너리·라이선스·조회 정의 파일은 이 저장소에 재배포하지 않는다.

| 추출 파일 | 크기 | SHA256 |
| --- | ---: | --- |
| giexpertcontrol.ocx | 61,440 | `7104c5af1de55781b93eae312dc51178c0faa082a2afa187f80b4aee616ac087` |
| giexpertstarter.exe | 231,296 | `234d3fcc02e827d084d51d6923c77a22969db5e60452e661bf145378cce8714b` |
| giexpertmain.exe | 106,880 | `d508b8f6b7d3a1898ab3a2625d8ef9ec9abff23aefbf36899bfaa78974362be1` |

## GI004가 설정되는 실제 분기

해당 x86 OCX의 MFC dispatch map에서 `RequestData`는 VA `0x10002510`이다. 이 함수는 `FindWindowA`로 찾은 `GiExpertMainApp-CLASS` 수신 창에 `SendMessageA`를 보낸다. 메시지는 `WM_COPYDATA(0x004A)`이며 요청 이름과 입력을 포함한다.

`0x10002630`에서 SendMessageA를 호출한 뒤, 반환값의 하위16비트를 비교한다. `0x1000264C`의 양수 분기로 들어가지 못하면 `0x1000264E`에서 오류 번호4를 설정하고0을 반환한다. 오류 생성 함수는 `GI%03d` 형식을 사용하므로 이것이 `GI004`다. 이 분기의 메시지가 `조회 데이터 파싱 오류 입니다.`이다.

따라서 **수신 프로세스가0을 반환하거나 Windows가 메시지 전달을 막은 경우에도 같은 GI004가 나온다.** GI004만으로 데이터 파일 파싱 실패, 이벤트 싱크 실패 또는 사용자 설정 오류를 구분할 수 없다. 이 바이너리의 수락된 RequestData ID는 양수이며 실패 경로는0이다.

## Windows 권한 차이

추출한 `giexpertstarter.exe`의 내장 manifest는 `requestedExecutionLevel=requireAdministrator`다. `giexpertmain.exe`와 `indihelper.exe`는 `asInvoker`이므로 시작 프로세스의 권한을 상속한다. 기존 자체 EXE는 이 수준을 요청하지 않았다.

Windows UIPI는 낮은 integrity level의 프로세스가 높은 프로세스에 보내는 메시지를 제한한다. [Microsoft SendMessage 문서](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-sendmessagew)는 UIPI 차단 시 GetLastError가5라고 설명한다. 이 경우 신한 OCX의 위 분기에서도0을 받아 GI004를 설정할 수 있다.

1.4.0은 EXE의 실행 수준을 공식 시작기와 맞추고, 실제 앱/Indi 프로세스 수준 및 요청 직후 Win32 오류를 로그에 남긴다. Windows 메시지 필터나 다른 프로세스의 ACL을 완화하지 않는다.

## 검증의 범위

`tests/uipi`는 고권한 수신 창과 별도 송신 프로세스로 동일 권한의 전송 성공, 낮은 권한의 전송 차단0/오류5, 수신 함수 미실행을 검사한다. 이는 운영체제의 실제 프로세스 경계를 사용하며 신한 서버를 흉내 낸 성공 판정은 아니다.

이후 사용자 1.4.0 로그에서 앱/Indi 모두 `High(12288)`, `appBelowReceiver=False`, `RequestData rid=3`, `Win32Error=0`, `NATIVE ReceiveData rid=3`이 확인됐다. 이 요청의 메시지 전달·이벤트 수신은 작동했다. 다만 앱이 응답 전에 오류 상태2로 중단한 탓에 종목 행 파싱과 화면 조회 성공은 확인하지 못했다. 실주문은 계속 비활성화한다.

## 1.4.1의 응답 상태 판정 시점

같은 OCX의 `RequestData`는 시작 부분 `0x10002516`에서 `0x10003950`을 호출해 응답 상태·오류 코드·메시지를 지운다. `GetErrorState`(`0x100034F0`)는 연결된 상태에서 상태 문자열이 `N`이면0, 그 밖에는2를 반환하므로 응답 전의 빈 상태도2다. 요청 직후 상태2만으로 실패를 판정한 앱 코드가 잘못됐다.

수신 경로는 오류일 때 `0x10001C76`에서 상태 `E`, 성공일 때 `0x10001CB4`에서 `N`을 기록한 뒤 `0x10001D26`에서 DISPID1의 `ReceiveData`를 발생시킨다. 따라서1.4.1은 양수 요청번호를 접수 기준으로 사용하고, 콜백 안에서 오류 상태와 데이터를 복사한 뒤 요청번호가 일치하는 스냅샷만 처리한다. 요청 반환 전의 재진입 이벤트도 같은 규칙을 따른다. 오류 응답의 빈 잔고를 성공으로 처리하지 않는다.

추출 바이너리의 정적 분석과 사용자 로그가 이 수정의 근거다. 수정 후 실제 종목 행 파싱, 다른 조회 및 HTS 값 대조는 별도로 확인해야 한다.
