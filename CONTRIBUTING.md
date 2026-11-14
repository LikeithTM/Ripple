# Contributing to Ripple

Thank you for helping build Ripple!

## Development Workflow
1. Ensure the .NET 8 Desktop SDK is installed (`dotnet --info`).
2. Build and run all unit, integration, and 24-hour soak tests:
   ```powershell
   .\scripts\build.ps1 -Configuration Debug -RunTests
   ```
3. Follow existing architectural boundaries:
   - Keep pure math, state machines, geometry builders, parsers, and HTTP servers inside `Ripple.Core` (platform-agnostic & unit-testable without a WPF `Dispatcher`).
   - Isolate Windows P/Invoke, WinRT, and WPF visuals inside `Ripple.App`.
   - Maintain per-monitor DPI v2 awareness and `MA_NOACTIVATE` focus preservation on all overlay windows.

## Pull Request Checklist
- `dotnet build Ripple.sln -c Release` succeeds with 0 errors.
- `dotnet test Ripple.sln -c Release` passes all tests including `Simulated24HourSoakTest_BoundedMemoryZeroLeaksZeroExceptions`.
- Any new module implements `IRippleModule` and degrades gracefully when a device or OS permission is unavailable.
