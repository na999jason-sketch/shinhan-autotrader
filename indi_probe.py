"""Read-only Indi stock_mst connection probe, matching Shinhan PyQt example.

No trading/credentials functions are instantiated or called. Run as a child process
via the existing app.py --probe-indi arguments. Python 3.11 x86 / PyQt5 x86.
"""
from pathlib import Path
import faulthandler
import json
import os
import struct
import sys
import time
import traceback

PROG_ID = 'GIEXPERTCONTROL.GiExpertControlCtrl.1'


def append_step(path, stage):
    with Path(path).open('a', encoding='utf-8') as f:
        f.write(time.strftime('%Y-%m-%d %H:%M:%S') + ' | ' + str(stage) + '\n')
        f.flush()
        os.fsync(f.fileno())


def write_result(path, status, **data):
    dest = Path(path)
    temp = Path(str(path)+'.tmp')
    temp.write_text(json.dumps({'status':status, **data},ensure_ascii=False),encoding='utf-8')
    os.replace(temp,dest)


def probe_command(executable, app_path, log_path, result_path, frozen):
    if frozen:
        return [executable, '--probe-indi', log_path, result_path]
    return [executable, app_path, '--probe-indi', log_path, result_path]


def exit_detail(code):
    if code is None: return '진단 실행 중'
    unsigned=code & 0xFFFFFFFF
    if unsigned==0xC0000005: return '0xC0000005 (Windows/OCX 접근 위반 가능성)'
    if unsigned==0xC0000409: return '0xC0000409 (Windows 네이티브 예외)'
    return '종료 코드 '+str(code)+f' (0x{unsigned:08X})'


def run_probe(log_path, result_path):
    """Compare the minimum documented PyQt-QAxWidget flow to Indi Helper.

    Key difference vs prior probe: create *unparented* QAxWidget in QMainWindow
    just as in publicly documented Shinhan Indi PyQt reference example. The
    ActiveX control is not embedded into QWidget/layout as in the old probe.
    """
    append_step(log_path, 'START bits='+str(struct.calcsize('P')*8))
    fatal_log = open(log_path+'.fatal.txt','a',encoding='utf-8')
    faulthandler.enable(file=fatal_log, all_threads=True)
    try:
        if struct.calcsize('P') != 4:
            write_result(result_path,'ERROR',reason='32비트 Python/EXE가 아닙니다')
            return 3
        from PyQt5.QtCore import QTimer
        from PyQt5.QtWidgets import QApplication, QMainWindow, QWidget, QVBoxLayout, QLabel
        from PyQt5.QAxContainer import QAxWidget

        app = QApplication.instance() or QApplication([])
        state={'done':False,'rid':None,'requested':False}

        def finish(status, **detail):
            if state['done']: return
            state['done']=True
            append_step(log_path,'FINISH status='+status+' reason='+str(detail.get('reason',''))[:200])
            write_result(result_path,status,**detail)
            QTimer.singleShot(150, app.quit)

        class ProbeWindow(QMainWindow):
            def __init__(self):
                super().__init__()
                self.setWindowTitle('신한i indi 종목조회 - 읽기 전용 API 검사')
                content=QWidget(self)
                layout=QVBoxLayout(content)
                layout.addWidget(QLabel('신한i indi stock_mst 종목조회 이벤트 검사 중\n매수·매도 주문은 보내지 않습니다.'))
                self.setCentralWidget(content)
                # Important: follow official Python sample: no parent argument.
                self.IndiTR=QAxWidget(PROG_ID)
                append_step(log_path,'QAX_CONSTRUCTED isNull='+str(self.IndiTR.isNull()))
                if not self.IndiTR.isNull():
                    self.IndiTR.ReceiveData.connect(self.ReceiveData)
                    self.IndiTR.ReceiveSysMsg.connect(self.ReceiveSysMsg)
                    append_step(log_path,'EVENTS_CONNECTED')
                self.resize(455,120)

            def RequestStocks(self):
                if state['done'] or state['requested']:return
                if self.IndiTR.isNull():
                    finish('ERROR',reason='신한 ActiveX 컨트롤을 생성할 수 없습니다')
                    return
                state['requested']=True
                try:
                    append_step(log_path,'SET_QUERY_NAME_ENTER')
                    ret=self.IndiTR.dynamicCall('SetQueryName(QString)', 'stock_mst')
                    append_step(log_path,'SET_QUERY_NAME_RETURN='+repr(ret))
                    rid=self.IndiTR.dynamicCall('RequestData()')
                    append_step(log_path,'REQUEST_DATA_RETURN='+repr(rid))
                    if rid is None:
                        finish('ERROR',reason='RequestData 반환값 없음')
                        return
                    state['rid']=int(rid)
                    if state['rid']<0:
                        finish('ERROR',reason='RequestData 오류 ID '+str(rid))
                except BaseException as exc:
                    append_step(log_path,'REQUEST_EXCEPTION='+traceback.format_exc()[-1500:])
                    finish('ERROR',reason=type(exc).__name__+': '+str(exc))

            def ReceiveData(self,rqid):
                append_step(log_path,'RECEIVE_DATA rid='+str(rqid)+' expected='+str(state['rid']))
                if state['done']:return
                try:
                    if state['rid'] is not None and int(rqid)!=state['rid']:
                        append_step(log_path,'IGNORE_DIFFERENT_ID')
                        return
                    n=int(self.IndiTR.dynamicCall('GetMultiRowCount()') or 0)
                    append_step(log_path,'STOCK_ROW_COUNT='+str(n))
                    if n<1:
                        finish('ERROR',reason='ReceiveData 수신했으나 0건')
                        return
                    rows=[]
                    for i in range(min(3,n)):
                        code=str(self.IndiTR.dynamicCall('GetMultiData(int, int)',i,1) or '').strip()
                        name=str(self.IndiTR.dynamicCall('GetMultiData(int, int)',i,3) or '').strip()
                        rows.append({'code':code,'name':name})
                    finish('OK',count=n,samples=rows)
                except BaseException as exc:
                    append_step(log_path,'RECEIVE_EXCEPTION='+traceback.format_exc()[-1500:])
                    finish('ERROR',reason=type(exc).__name__+': '+str(exc))

            def ReceiveSysMsg(self,msg):
                append_step(log_path,'RECEIVE_SYS_MSG='+str(msg))

        window=ProbeWindow()
        if window.IndiTR.isNull():
            finish('ERROR',reason='ActiveX 생성 실패')
        else:
            window.show()
            append_step(log_path,'MAIN_WINDOW_VISIBLE')
            QTimer.singleShot(800,window.RequestStocks)
            QTimer.singleShot(25000,lambda:finish('TIMEOUT',reason=
                '독립 PyQt ActiveX 객체에서 ReceiveData가 수신되지 않음. '
                'Indi Helper는 정상이며 Windows/PyQt COM 이벤트 환경 추가 확인 필요'))
        app.exec_()
        return 0 if state['done'] else 2
    except BaseException as exc:
        append_step(log_path,'FATAL_EXCEPTION='+traceback.format_exc()[-2000:])
        write_result(result_path,'ERROR',reason=type(exc).__name__+': '+str(exc))
        return 3
    finally:
        fatal_log.close()
