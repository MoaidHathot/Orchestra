@echo off
REM Quick start Orchestra Portal with Dev Tunnel
REM 
REM This exposes your local server to the internet for:
REM   - Power Automate webhook testing
REM   - Azure Logic Apps integration
REM   - Mobile device testing
REM
REM First run will prompt you to login to devtunnel.

PowerShell -ExecutionPolicy Bypass -File "%~dp0start-with-tunnel.ps1" %*
