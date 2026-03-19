@echo off
REM Run all Orchestra tests

echo ================================
echo Orchestra Full Test Suite
echo ================================

REM Change to repository root
cd /d "%~dp0\.."

echo.
echo 1. Building solution...
echo --------------------------------
dotnet build
if errorlevel 1 (
    echo Build failed!
    exit /b 1
)

echo.
echo 2. Running Engine Unit Tests...
echo --------------------------------
dotnet test tests/Orchestra.Engine.Tests --no-build --logger "console;verbosity=detailed"
if errorlevel 1 (
    echo Engine tests failed!
    exit /b 1
)

echo.
echo 3. Running Host Unit Tests...
echo --------------------------------
dotnet test tests/Orchestra.Host.Tests --no-build --logger "console;verbosity=detailed"
if errorlevel 1 (
    echo Host tests failed!
    exit /b 1
)

echo.
echo 4. Running Copilot Unit Tests...
echo --------------------------------
dotnet test tests/Orchestra.Copilot.Tests --no-build --logger "console;verbosity=detailed"
if errorlevel 1 (
    echo Copilot tests failed!
    exit /b 1
)

echo.
echo 5. Running Mcp.Graph Unit Tests...
echo --------------------------------
dotnet test tests/Orchestra.Mcp.Graph.Tests --no-build --logger "console;verbosity=detailed"
if errorlevel 1 (
    echo Mcp.Graph tests failed!
    exit /b 1
)

echo.
echo ================================
echo All unit tests completed successfully!
echo ================================
echo.
echo NOTE: Portal integration and E2E tests are Windows-only and require separate execution:
echo   dotnet test tests/Orchestra.Portal.Tests
echo   dotnet test tests/Orchestra.Portal.E2E  (requires running Portal server)
