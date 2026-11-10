@echo off
chcp 65001 >nul
py -3.11-32 app.py
if errorlevel 1 pause
