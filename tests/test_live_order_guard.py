"""Exercise legacy order entry points without a Windows COM installation."""
import importlib.util
from pathlib import Path
import sys
from types import ModuleType
import unittest
from unittest.mock import Mock, patch


def load_client_without_com():
    # Import the real adapter with only the optional UI dependency replaced.
    core = ModuleType('PyQt5.QtCore')
    core.QObject = object
    core.QTimer = Mock()
    core.pyqtSignal = Mock(return_value=None)
    ax = ModuleType('PyQt5.QAxContainer')
    ax.QAxWidget = Mock(side_effect=AssertionError('COM must never be created'))
    path = Path(__file__).resolve().parents[1] / 'indi_client.py'
    spec = importlib.util.spec_from_file_location('guard_test_indi_client', path)
    module = importlib.util.module_from_spec(spec)
    with patch.dict(sys.modules, {'PyQt5': ModuleType('PyQt5'), 'PyQt5.QtCore': core, 'PyQt5.QAxContainer': ax}):
        spec.loader.exec_module(module)
    return module.IndiClient


class LiveOrderGuardTests(unittest.TestCase):
    def setUp(self):
        self.client = load_client_without_com()
        self.transport = Mock()

    def test_public_order_is_blocked_before_transport(self):
        with self.assertRaisesRegex(RuntimeError, '실주문 비활성'):
            self.client.order(self.transport, 'account', 'secret', 'BUY', '005930', 1)
        self.assertEqual(self.transport.mock_calls, [])

    def test_generic_order_request_cannot_bypass_guard(self):
        with self.assertRaisesRegex(RuntimeError, '실주문 비활성'):
            self.client.request(self.transport, 'order', [(0, 'account'), (2, 'secret')])
        self.assertEqual(self.transport.mock_calls, [])

    def test_guard_does_not_disclose_account_credentials(self):
        with self.assertRaises(RuntimeError) as caught:
            self.client.order(self.transport, 'private-account', 'private-password', 'SELL', '005930', 10)
        self.assertNotIn('private-account', str(caught.exception))
        self.assertNotIn('private-password', str(caught.exception))


if __name__ == '__main__':
    unittest.main()
