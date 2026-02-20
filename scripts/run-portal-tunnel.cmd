@echo off
REM Orchestra Portal with Dev Tunnel
REM This script starts the portal server with a public dev tunnel.
REM 
REM Prerequisites:
REM   1. Install devtunnel: winget install Microsoft.devtunnel
REM   2. Login once: devtunnel user login
REM
REM Usage:
REM   run-portal-tunnel.cmd                    - Default (port 5100, anonymous access)
REM   run-portal-tunnel.cmd -Port 5200         - Custom port
REM   run-portal-tunnel.cmd -Persistent        - Stable URL across restarts

PowerShell -ExecutionPolicy Bypass -File "%~dp0run-portal-tunnel.ps1" %*
