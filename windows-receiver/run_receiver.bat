@echo off
REM CordMic receiver - ganti "CABLE Input" sesuai nama device virtual cable kamu
cd /d %~dp0
python -m cordmic_receiver --device "CABLE Input" --jitter-ms 10 --tray --log cordmic.log %*
