#!/bin/bash
# Run all Orchestra Portal tests

set -e

echo "================================"
echo "Orchestra Portal Test Suite"
echo "================================"

# Change to repository root
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.."

echo ""
echo "1. Installing Playwright browsers..."
echo "--------------------------------"
pwsh -Command "& { cd tests/Orchestra.Portal.E2E; dotnet build; playwright install chromium }" 2>/dev/null || \
    dotnet tool run playwright install chromium --project tests/Orchestra.Portal.E2E 2>/dev/null || \
    echo "Note: Playwright browsers may need to be installed manually: npx playwright install"

echo ""
echo "2. Running Integration Tests..."
echo "--------------------------------"
dotnet test tests/Orchestra.Portal.Tests --logger "console;verbosity=detailed"

echo ""
echo "3. Running E2E Tests..."
echo "--------------------------------"
echo "Note: E2E tests require the Portal server to be running on http://localhost:5099"
echo "Start the server with: dotnet run --project playground/Hosting/Orchestra.Playground.Copilot.Portal --urls http://localhost:5099"
echo ""
dotnet test tests/Orchestra.Portal.E2E --logger "console;verbosity=detailed"

echo ""
echo "================================"
echo "All tests completed!"
echo "================================"
