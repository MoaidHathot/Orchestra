#!/bin/bash
# Run all Orchestra tests

set -e

echo "================================"
echo "Orchestra Full Test Suite"
echo "================================"

# Change to repository root
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.."

echo ""
echo "1. Building solution..."
echo "--------------------------------"
dotnet build

echo ""
echo "2. Running Engine Unit Tests..."
echo "--------------------------------"
dotnet test tests/Orchestra.Engine.Tests --no-build --logger "console;verbosity=detailed"

echo ""
echo "3. Running Host Unit Tests..."
echo "--------------------------------"
dotnet test tests/Orchestra.Host.Tests --no-build --logger "console;verbosity=detailed"

echo ""
echo "4. Running Copilot Unit Tests..."
echo "--------------------------------"
dotnet test tests/Orchestra.Copilot.Tests --no-build --logger "console;verbosity=detailed"

echo ""
echo "================================"
echo "All unit tests completed successfully!"
echo "================================"
echo ""
echo "NOTE: Portal integration and E2E tests require separate execution:"
echo "  dotnet test tests/Orchestra.Portal.Tests"
echo "  dotnet test tests/Orchestra.Portal.E2E  (requires running Portal server)"
