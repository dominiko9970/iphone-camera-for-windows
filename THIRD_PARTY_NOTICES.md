# Third-Party Notices

This file documents third-party libraries and frameworks used by this repository.

## NuGet Dependencies (Production Code)

### Makaretu.Dns.Multicast (0.27.0)

- Used for mDNS/DNS-SD advertisement and discovery on Windows.
- Project: https://github.com/richardschneider/net-mdns
- Package: https://www.nuget.org/packages/Makaretu.Dns.Multicast

### System.Text.Json (8.0.5)

- Used for JSON serialization/deserialization in protocol and control messages.
- Project: https://github.com/dotnet/runtime
- Package: https://www.nuget.org/packages/System.Text.Json

## NuGet Dependencies (Test-Only)

### coverlet.collector (6.0.0)

- Used for test coverage collection.
- Project: https://github.com/coverlet-coverage/coverlet
- Package: https://www.nuget.org/packages/coverlet.collector

### Microsoft.NET.Test.Sdk (17.8.0)

- Test host and discovery infrastructure for .NET tests.
- Project: https://github.com/microsoft/vstest
- Package: https://www.nuget.org/packages/Microsoft.NET.Test.Sdk

### xunit (2.5.3)

- Unit testing framework used by `tests/UniversalCam.Tests`.
- Project: https://github.com/xunit/xunit
- Package: https://www.nuget.org/packages/xunit

### xunit.runner.visualstudio (2.5.3)

- Visual Studio / `dotnet test` runner integration for xUnit.
- Project: https://github.com/xunit/visualstudio.xunit
- Package: https://www.nuget.org/packages/xunit.runner.visualstudio

## Platform SDK Frameworks

These are platform-provided frameworks, not bundled third-party source dependencies:

- Apple AVFoundation
- Apple VideoToolbox
- Apple Network.framework
- .NET runtime libraries (including `System.Net.Quic`)
- Windows Media Foundation

## Contributor Covenant Attribution

The project code of conduct is adapted from Contributor Covenant v2.1.
See `CODE_OF_CONDUCT.md` for attribution details.

## Notes

- Versions listed here are the versions currently referenced in project files.
- License terms for each dependency are governed by the corresponding upstream project/package.
- If dependencies are updated, please update this file in the same pull request.
