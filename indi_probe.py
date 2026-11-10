"""Isolated, read-only Shinhan Indi ActiveX diagnostics (v0.3.3).

This process NEVER creates an order-capable client, never accesses account
passwords, and never submits trading orders. An actual HWND is created for
QAxWidget and the request is posted only after the Qt event loop starts.
"""
import faulthandler
import json
import os
from pathlib import Path
import struct
import sys
import time
import traceback


PROG_ID = 'GIEXPERTCONTROL.GiExpertControlCtrl.1'


def append_step(path, stage):
    with Path(path).open('a', encoding='utf-8') as out:
        out.write(time.strftime('%Y-%m-%d %H:%M:%S') + ' | ' + str(stage) + '\n')
        out.flush()
        os.fsync(out.fileno())


def write_result(path, status, **kwargs):
    tmp = Path(str(path) + '.tmp')
    tmp.write_text(json.dumps({'status': status, **kwargs}, ensure_ascii=False), encoding='utf-8')
    os.replace(tmp, path)


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
    append_step(log_path, '01 CHILD_START bits=' + str(struct.calcsize('P') * 8) +
                ' frozen=' + str(bool(getattr(sys, 'frozen', False))))
    crashlog = open(log_path + '.fatal.txt', 'a', encoding='utf-8')
    faulthandler.enable(file=crashlog, all_threads=True)
    try:
        append_step(log_path, '02 IMPORT_QT')
        from PyQt5.QtCore import QTimer
        from PyQt5.QtWidgets import QApplication, QWidget, QVBoxLayout, QLabel
        from PyQt5.QAxContainer import QAxWidget
        append_step(log_path, '03 IMPORT_QT_OK')
        if struct.calcsize('P') != 4:
            raise RuntimeError('신한 OCX 진단에는 32비트 환경이 필요합니다.')
        app = QApplication.instance() or QApplication([])
        state = {'finished': False, 'ocx': None, 'window': None,
                 'request_id': None, 'query': None, 'round': 0}

        def finish(status, **details):
            if state['finished']:
                return
            state['finished'] = True
            append_step(log_path, 'FINISH ' + status + ' reason=' +
                        str(details.get('reason', ''))[:200])
            write_result(result_path, status, **details)
            QTimer.singleShot(100, app.quit)

        def on_receive(rid):
            try:
                append_step(log_path, '09 RECEIVE_DATA rid=' + str(rid) +
                            ' expected=' + str(state['request_id']) +
                            ' query=' + str(state['query']))
                if state['request_id'] is not None and int(rid) != state['request_id']:
                    append_step(log_path, '09A_UNEXPECTED_RID')
                    return
                ocx = state['ocx']
                append_step(log_path, '10 GET_MULTI_ROW_COUNT_ENTER')
                n = ocx.dynamicCall('GetMultiRowCount()')
                append_step(log_path, '11 GET_MULTI_ROW_COUNT_RETURN=' + repr(n))
                count = int(n or 0)
                if state['query'] == 'stock_mst':
                    if count <= 0:
                        finish('ERROR', reason='stock_mst 응답이 왔지만 종목수가 0건입니다')
                        return
                    sample = []
                    for i in range(min(count, 3)):
                        code = str(ocx.dynamicCall('GetMultiData(int, int)', i, 1) or '').strip()
                        name = str(ocx.dynamicCall('GetMultiData(int, int)', i, 3) or '').strip()
                        if code and name:
                            sample.append({'code': code, 'name': name})
                    append_step(log_path, '12 STOCK_SAMPLE_OK')
                    finish('OK', count=count, samples=sample)
                elif state['query'] == 'AccountList':
                    # Never read or log account numbers or names. Just test that
                    # the OCX callback and query path are functional.
                    finish('PARTIAL', reason='AccountList 응답 수신, stock_mst 응답은 없음',
                           account_row_count=count)
                else:
                    finish('ERROR', reason='예상하지 못한 조회 이벤트')
            except BaseException as exc:
                append_step(log_path, 'RECEIVE_EXCEPTION ' + traceback.format_exc()[-1200:])
                finish('ERROR', reason=type(exc).__name__ + ': ' + str(exc))

        def on_system(msg):
            append_step(log_path, 'SYS_MESSAGE=' + str(msg))

        def request_query(name):
            if state['finished']:
                return
            try:
                state['query'] = name
                state['request_id'] = None
                state['round'] += 1
                append_step(log_path, '06 SET_QUERY_NAME_ENTER ' + name)
                ret = state['ocx'].dynamicCall('SetQueryName(QString)', name)
                append_step(log_path, '07 SET_QUERY_NAME_RETURN=' + repr(ret))
                rid = state['ocx'].dynamicCall('RequestData()')
                append_step(log_path, '08 REQUEST_DATA_RETURN rid=' + repr(rid) +
                            ' query=' + name)
                if rid is None:
                    finish('ERROR', reason=name+' RequestData 반환값 없음')
                    return
                state['request_id'] = int(rid)
                if state['request_id'] < 0:
                    finish('ERROR', reason=name+' 요청 거부, 요청 ID='+str(rid))
            except BaseException as exc:
                append_step(log_path, 'REQUEST_EXCEPTION '+traceback.format_exc()[-1200:])
                finish('ERROR', reason=type(exc).__name__ + ': ' + str(exc))

        def fallback():
            if state['finished'] or state['query'] != 'stock_mst':
                return
            append_step(log_path, '13 STOCK_TIMEOUT; TRY_ACCOUNTLIST_METADATA_ONLY')
            request_query('AccountList')

        def start():
            try:
                append_step(log_path, '04 CREATE_OCX ' + PROG_ID)
                win = QWidget()
                win.setWindowTitle('신한i indi 읽기 전용 연결 진단 — 주문 없음')
                layout = QVBoxLayout(win)
                layout.addWidget(QLabel('신한i indi의 조회 응답을 확인 중입니다.\n'
                                        '이 창에서는 실제 매수·매도 주문을 보내지 않습니다.'))
                ocx = QAxWidget(PROG_ID, win)
                layout.addWidget(ocx)
                state['window'] = win
                state['ocx'] = ocx
                append_step(log_path, '05 CREATE_OCX_RETURN isNull=' + str(ocx.isNull()))
                if ocx.isNull():
                    finish('ERROR', reason='신한 OCX 생성 실패(등록, 권한, 비트수 확인)')
                    return
                ocx.ReceiveData.connect(on_receive)
                ocx.ReceiveSysMsg.connect(on_system)
                win.resize(390, 130)
                win.show()
                append_step(log_path, '05A WIDGET_VISIBLE=' + str(ocx.isVisible())+
                            ' WINID='+str(int(ocx.winId())))
                QTimer.singleShot(550, lambda: request_query('stock_mst'))
            except BaseException as exc:
                append_step(log_path, 'START_EXCEPTION '+traceback.format_exc()[-1200:])
                finish('ERROR', reason=type(exc).__name__ + ': ' + str(exc))

        QTimer.singleShot(0, start)
        QTimer.singleShot(13500, fallback)
        QTimer.singleShot(30000, lambda: finish('TIMEOUT', reason=
            'stock_mst와 AccountList 모두 ReceiveData 이벤트가 오지 않음. '
            'OCX 이벤트/로그인 세션/사용계약 확인 필요')
            if not state['finished'] else None)
        app.exec_()
        return 0 if state['finished'] else 2
    except BaseException as exc:
        append_step(log_path, 'FATAL_PYTHON_EXCEPTION '+traceback.format_exc()[-1500:])
        write_result(result_path, 'ERROR', reason=type(exc).__name__+': '+str(exc))
        return 3
    finally:
        crashlog.close()
