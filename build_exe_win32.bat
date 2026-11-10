@echo off
chcp 65001 >nul
py -3.11-32 -m pip install -r requirements-build.txt
if errorlevel 1 (echo Dependency install failed & pause & exit /b 1)
py -3.11-32 -m PyInstaller --noconfirm --clean --onefile --windowed --name ShinhanAutoTrader_v03 --hidden-import PyQt5.QAxContainer app.py
if errorlevel 1 (echo Build failed & pause & exit /b 1)
echo Done: dist\ShinhanAutoTrader_v03.exe
pause
