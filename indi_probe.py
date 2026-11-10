"""Isolated read-only Shinhan Indi connection diagnostic.

Only the diagnostic subprocess loads QAxContainer / Shinhan OCX. The parent
never loads the OCX, so a native COM access violation cannot kill the GUI.
No account details are accepted, read or logged; no order code is reachable.
"""
import json
import os
from pathlib import Path
import struct
import sys
import time
import traceback


def append_step(path, stage):
    with Path(path).open('a', encoding='utf-8') as f:
        f.write(time.strftime('%Y-%m-%d %H:%M:%S') + ' | ' + str(stage) + '\n')
        f.flush()
        os.fsync(f.fileno())


def write_result(path, status, **kwargs):
    out = {'status': status, **kwargs}
    tmp = Path(str(path) + '.tmp')
    tmp.write_text(json.dumps(out, ensure_ascii=False), encoding='utf-8')
    os.replace(str(tmp), str(path))


def probe_command(executable, app_path, log_path, result_path, frozen):
    if frozen:
        return [executable, '--probe-indi', log_path, result_path]
    return [executable, app_path, '--probe-indi', log_path, result_path]


def exit_detail(code):
    if code is None:
        return '진단 실행 중'
    unsigned = code & 0xFFFFFFFF
    if unsigned == 0xC0000005:
        return '0xC0000005 (OCX/Windows 네이티브 접근 위반 의심)'
    if unsigned == 0xC0000409:
        return '0xC0000409 (Windows 네이티브 보호 예외)'
    return '종료 코드 ' + str(code) + f' (0x{unsigned:08X})'


def run_probe(log_path, result_path):
    append_step(log_path, '01 CHILD_START bitness=' + str(struct.calcsize('P') * 8) +
                ', frozen=' + str(bool(getattr(sys, 'frozen', False))))
    # Native exceptions may not be translated to Python exceptions. Persist
    # the last completed native call as the best available diagnostic clue.
    faulthandler_log = open(str(log_path) + '.fatal.txt', 'a', encoding='utf-8')
    import faulthandler
    faulthandler.enable(file=faulthandler_log, all_threads=True)
    try:
        append_step(log_path, '02 IMPORT_QT')
        from PyQt5.QtCore import QTimer
        from PyQt5.QtWidgets import QApplication
        from PyQt5.QAxContainer import QAxWidget
        append_step(log_path, '03 IMPORT_QT_OK')
        if struct.calcsize('P') != 4:
            raise RuntimeError('이 진단용 신한 OCX는 32비트 EXE를 예상합니다. 현재 ' +
                               str(struct.calcsize('P') * 8) + '비트입니다.')
        app = QApplication.instance() or QApplication([])
        state = {'finished': False, 'request_id': None, 'ocx': None}

        def finish(status, **details):
            if state['finished']:
                return
            state['finished'] = True
            append_step(log_path, 'FINISH ' + status)
            write_result(result_path, status, **details)
            QTimer.singleShot(100, app.quit)

        def on_receive(rid):
            try:
                append_step(log_path, '09 RECEIVE_DATA rid=' + str(rid))
                if state['request_id'] is not None and int(rid) != state['request_id']:
                    append_step(log_path, '09A_OTHER_REQUEST')
                    return
                ocx = state['ocx']
                append_step(log_path, '10 GET_MULTI_ROW_COUNT_ENTER')
                n = ocx.dynamicCall('GetMultiRowCount()')
                append_step(log_path, '11 GET_MULTI_ROW_COUNT_OK ' + str(n))
                count = int(n)
                if count <= 0:
                    finish('ERROR', reason='종목 응답 0건입니다. 서비스 사용신청/OCX 연결/서버 응답 확인 필요')
                    return
                samples = []
                for i in range(min(count, 5)):
                    code = str(ocx.dynamicCall('GetMultiData(int, int)', i, 1) or '').strip()
                    name = str(ocx.dynamicCall('GetMultiData(int, int)', i, 3) or '').strip()
                    if code and name:
                        samples.append({'code': code, 'name': name})
                append_step(log_path, '12 SAMPLE_ROWS_OK')
                finish('OK', count=count, samples=samples)
            except Exception as ex:
                append_step(log_path, 'RECEIVE_EXCEPTION ' + type(ex).__name__)
                finish('ERROR', reason=type(ex).__name__ + ': ' + str(ex))

        def on_system(message):
            # System messages can be informational; log them without assuming fatality.
            append_step(log_path, 'SYSTEM_MESSAGE ' + str(message))

        def start():
            try:
                append_step(log_path, '04 CREATE_OCX_ENTER GIEXPERTCONTROL.GiExpertControlCtrl.1')
                ocx = QAxWidget('GIEXPERTCONTROL.GiExpertControlCtrl.1')
                state['ocx'] = ocx
                append_step(log_path, '05 CREATE_OCX_RETURN isNull=' + str(ocx.isNull()))
                if ocx.isNull():
                    finish('ERROR', reason='신한 32비트 OCX 생성 실패(등록/권한/아키텍처 확인)')
                    return
                ocx.ReceiveData.connect(on_receive)
                ocx.ReceiveSysMsg.connect(on_system)
                append_step(log_path, '06 SET_QUERY_NAME_ENTER')
                ocx.dynamicCall('SetQueryName(QString)', 'stock_mst')
                append_step(log_path, '07 SET_QUERY_NAME_OK; REQUEST_DATA_ENTER')
                rid = ocx.dynamicCall('RequestData()')
                append_step(log_path, '08 REQUEST_DATA_RETURN rid=' + str(rid))
                if rid is None or int(rid) < 0:
                    finish('ERROR', reason='신한 종목조회 요청 거부: ' + str(rid))
                    return
                state['request_id'] = int(rid)
            except Exception as ex:
                append_step(log_path, 'START_EXCEPTION ' + type(ex).__name__)
                finish('ERROR', reason=type(ex).__name__ + ': ' + str(ex))

        QTimer.singleShot(0, start)
        QTimer.singleShot(22000, lambda: finish('TIMEOUT', reason='22초 동안 종목조회 응답 없음')
                           if not state['finished'] else None)
        app.exec_()
        return 0 if state['finished'] else 2
    except BaseException as ex:
        append_step(log_path, 'FATAL_PYTHON_EXCEPTION ' + type(ex).__name__)
        append_step(log_path, traceback.format_exc()[-4000:])
        write_result(result_path, 'ERROR', reason=type(ex).__name__ + ': ' + str(ex))
        return 3
    finally:
        faulthandler_log.close()
