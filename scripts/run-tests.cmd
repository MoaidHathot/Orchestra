@echo off
REM Run all Orchestra Portal tests

echo ================================
echo Orchestra Portal Test Suite
echo ================================

REM Change to repository root
cd /d "%~dp0\.."

echo.
echo 1. Building test projects...
echo --------------------------------
dotnet build tests/Orchestra.Portal.Tests
dotnet build tests/Orchestra.Portal.E2E

echo.
echo 2. Installing Playwright browsers...
echo --------------------------------
cd tests/Orchestra.Portal.E2E
dotnet tool restore 2>nul
pwsh -Command "playwright install chromium" 2>nul || echo Note: Run 'npx playwright install' if browsers are missing
cd ..\..

echo.
echo 3. Running Integration Tests...
echo --------------------------------
dotnet test tests/Orchestra.Portal.Tests --logger "console;verbosity=detailed"
if errorlevel 1 (
    echo Integration tests failed!
    exit /b 1
)

echo.
echo 4. Running E2E Tests...
echo --------------------------------
echo Note: E2E tests require the Portal server to be running on http://localhost:5099
echo Start the server with: dotnet run --project playground/Hosting/Orchestra.Playground.Copilot.Portal --urls http://localhost:5099
echo.
dotnet test tests/Orchestra.Portal.E2E --logger "console;verbosity=detailed"
if errorlevel 1 (
    echo E2E tests failed!
    exit /b 1
)

echo.
echo ================================
echo All tests completed successfully!
echo ================================
