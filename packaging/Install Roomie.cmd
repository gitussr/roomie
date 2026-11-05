@echo off
rem Double-click this. Windows asks for permission, then the script next to it does the rest.
powershell -NoProfile -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -NoExit -File \"%~dp0install-roomie.ps1\"'"
