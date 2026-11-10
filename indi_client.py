"""Shinhan i indi 32-bit ActiveX client. Windows/x86 PyQt5 required.

Exact responses and event registrations MUST be checked against the user's official
Indi interface spec; this file is an implementation, not a live-certified adapter.
"""
import re
from datetime import datetime
from PyQt5.QtCore import QObject, QTimer, pyqtSignal
from PyQt5.QAxContainer import QAxWidget
from engine import Bar, Holding, Quote, Symbol, normalize_code


class IndiClient(QObject):
    got = pyqtSignal(str, object)     # requested kind, parsed payload
    failed = pyqtSignal(str)
    event = pyqtSignal(str)

    PROG_ID = 'GIEXPERTCONTROL.GiExpertControlCtrl.1'

    def __init__(self, parent=None):
        super().__init__(parent)
        self.tr = QAxWidget(self.PROG_ID)
        self.real = QAxWidget(self.PROG_ID)
        if self.tr.isNull() or self.real.isNull():
            raise RuntimeError('32비트 신한i indi OCX를 찾을 수 없습니다. 공식 Indi를 설치·로그인하십시오.')
        self.tr.ReceiveData.connect(self._receive)
        self.tr.ReceiveSysMsg.connect(self._sys)
        self.real.ReceiveSysMsg.connect(self._sys)
        self.real.ReceiveRTData.connect(self._on_real)
        self.pending = None
        self.live_codes = set()
        self.account_registered = False
        self.timeout = QTimer(self)
        self.timeout.setSingleShot(True)
        self.timeout.timeout.connect(self._timed_out)

    def busy(self):
        return self.pending is not None

    @staticmethod
    def num(raw):
        try:return int(float(re.sub(r'[^0-9.\-]','',str(raw or ''))))
        except (ValueError,OverflowError): return 0

    @staticmethod
    def valid_exchange_clock(stamp, max_seconds):
        digits=re.sub(r'\D','',str(stamp))
        if len(digits)<6:return False
        try:
            hh,mm,ss=map(int,(digits[:2],digits[2:4],digits[4:6]))
            now=datetime.now()
            dt=now.replace(hour=hh,minute=mm,second=ss,microsecond=0)
            return abs((now-dt).total_seconds())<=max_seconds
        except ValueError:return False

    def _single(self, col):
        return self.tr.dynamicCall('GetSingleData(int)',col)

    def _multi(self, row, col):
        return self.tr.dynamicCall('GetMultiData(int, int)',row,col)

    def request(self, kind, fields=None):
        if self.busy(): return False
        qs = {'stocks':'stock_mst','bars':'TR_SCHART','quote':'SC','holdings':'SABA200QB','order':'SABA101U1'}
        if kind not in qs: raise ValueError('unsupported query '+kind)
        self.tr.dynamicCall('SetQueryName(QString)',qs[kind])
        for i,val in (fields or []):
            self.tr.dynamicCall('SetSingleData(int, QString)',int(i),str(val))
        rid = self.tr.dynamicCall('RequestData()')
        # TR callback is keyed on request ID, not assumed to be a sequential response.
        if rid is None or int(rid) < 0:
            self.failed.emit('신한 데이터 요청 접수 실패: '+qs[kind]+' / '+str(rid));return False
        self.pending = (kind,int(rid))
        self.timeout.start(20000)
        return True

    def load_stocks(self):
        return self.request('stocks')

    def load_bars(self, code):
        return self.request('bars',[(0,code),(1,'D'),(2,'1'),(3,'00000000'),(4,'99999999'),(5,'60')])

    def load_quote(self, code):
        return self.request('quote',[(0,code)])

    def load_holdings(self, account,password):
        return self.request('holdings',[(0,account),(1,'01'),(2,password)])

    def order(self, account,password,side,code,qty,price=0):
        if side not in ('BUY','SELL') or qty<1:raise ValueError('invalid order')
        # Market order is used only when explicitly armed in the GUI.
        # This requires conservative sizing; price is only a reference, not a cap.
        fields=[(0,account),(1,'01'),(2,password),(5,'0'),(6,'00'),
                (7,'2' if side=='BUY' else '1'),(8,'A'+code),(9,str(qty)),
                (11,'1'),(12,'1'),(13,'0'),(14,'0'),(21,'Y')]
        return self.request('order',fields)

    def subscribe(self, code):
        if code in self.live_codes: return
        ret=self.real.dynamicCall('RequestRTReg(QString, QString)','SC',code)
        if ret is None or int(ret)<0: raise RuntimeError('실시간시세 등록 실패: '+code)
        self.live_codes.add(code)

    def subscribe_account(self, account):
        # Whether the account key format is supported is broker-version-specific.
        # Treat this as a notification trigger; never interpret unknown fields as fills.
        for k in ('AA','AB','AD','AE'):
            ret=self.real.dynamicCall('RequestRTReg(QString, QString)',k,account)
            if ret is None or int(ret)<0:
                raise RuntimeError('주문/잔고 이벤트 '+k+' 등록 실패')
        self.account_registered=True

    def unsubscribe_all(self):
        for code in list(self.live_codes):
            try:self.real.dynamicCall('UnRequestRTReg(QString, QString)','SC',code)
            except Exception:pass
        self.live_codes.clear()
        # Do not claim subscriptions are removed; disconnect entirely by exiting app.

    def _timed_out(self):
        if self.pending is not None:
            kind,_=self.pending
            self.pending=None
            self.failed.emit('신한 '+kind+' 응답 20초 초과: 거래 중단, 주문 상태 HTS 확인')

    def _sys(self, msg):
        self.failed.emit('신한 시스템 상태 변화: '+str(msg)+' — 자동주문 중지')

    def _on_real(self, kind):
        kind=str(kind)
        # Events cause a new broker reconciliation; never use guessed event offsets.
        if kind in ('AA','AB','AD','AE'):
            self.event.emit('ACCOUNT_EVENT:'+kind)
            return
        if kind=='SC':
            try:
                code=normalize_code(self.real.dynamicCall('GetSingleData(int)',1))
                px=abs(self.num(self.real.dynamicCall('GetSingleData(int)',3)))
                segment=str(self.real.dynamicCall('GetSingleData(int)',17)).strip()
                stamp=str(self.real.dynamicCall('GetSingleData(int)',2)).strip()
                if code in self.live_codes and px>0 and self.valid_exchange_clock(stamp,30):
                    self.got.emit('tick',Quote(code,px,datetime.now(),segment=='1',stamp))
            except Exception as exc:
                self.failed.emit('실시간 시세 해석 오류: '+str(exc))

    def _receive(self, rid):
        if self.pending is None:return
        kind, expected=self.pending
        if int(rid)!=expected: return
        self.pending=None
        self.timeout.stop()
        try:
            count = self.num(self.tr.dynamicCall('GetMultiRowCount()'))
            if kind=='stocks':
                stocks=[]
                for i in range(min(count,6000)):
                    code=normalize_code(self._multi(i,1))
                    market=str(self._multi(i,2)).strip()
                    if (code and market in ('0','1') and str(self._multi(i,6)).strip()=='0'
                        and str(self._multi(i,7)).strip()=='0'
                        and str(self._multi(i,8)).strip()=='0'
                        and str(self._multi(i,14)).strip()=='ST'):
                        stocks.append(Symbol(code,str(self._multi(i,3)).strip(),market))
                if not stocks:raise ValueError('종목 조회 0건 — 스키마/인디 연결 확인')
                self.got.emit('stocks',stocks)
            elif kind=='bars':
                arr=[]
                for i in range(min(count,500)):
                    day=str(self._multi(i,0)).strip()
                    if re.fullmatch(r'\d{8}',day):
                        arr.append(Bar(day,self.num(self._multi(i,3)),self.num(self._multi(i,4)),
                                       self.num(self._multi(i,5)),self.num(self._multi(i,9))))
                self.got.emit('bars',arr)
            elif kind=='quote':
                code=normalize_code(self._single(1))
                px=abs(self.num(self._single(3)))
                stamp=str(self._single(2)).strip()
                reg=str(self._single(17)).strip()=='1'
                if not code or not px or not self.valid_exchange_clock(stamp,60):
                    raise ValueError('현재가/체결시각 응답 오류 또는 지연')
                self.got.emit('quote',Quote(code,px,datetime.now(),reg,stamp))
            elif kind=='holdings':
                out={}
                for i in range(min(count,1500)):
                    code=normalize_code(self._multi(i,0))
                    if not code:continue
                    out[code]=Holding(code,str(self._multi(i,1)).strip(),
                        self.num(self._multi(i,2)),self.num(self._multi(i,6)),
                        self.num(self._multi(i,4)),self.num(self._multi(i,3)),
                        self.num(self._multi(i,7)))
                self.got.emit('holdings',out)
            elif kind=='order':
                self.got.emit('order',{'number':str(self._single(0)).strip(),
                    'kind':str(self._single(2)).strip(),
                    'message':' '.join(str(self._single(i)) for i in (3,4,5))})
        except Exception as exc:
            self.failed.emit('신한 '+kind+' 응답 해석 오류: '+str(exc))
