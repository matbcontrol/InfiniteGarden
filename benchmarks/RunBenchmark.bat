@echo off
rem Automatic frame-budget benchmark: about 6 minutes (3 passes x 14 configurations), then a CSV appears next to this file.
rem Pick the GPU first: Windows Settings > System > Display > Graphics > InfiniteGarden.exe > Options.
cd /d "%~dp0"
echo Running benchmark, please do not touch the keyboard or mouse...
start "" /wait InfiniteGarden.exe -benchmark -screen-width 1920 -screen-height 1080 -screen-fullscreen 1
echo.
echo Results:
dir /b /o-d benchmark_*.csv
pause
