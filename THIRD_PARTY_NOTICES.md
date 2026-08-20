# Third-party notices

Original WPF Tools MCP code is licensed under the MIT License in `LICENSE`.
Packaged builds also redistribute parts of SnoopWPF under the Microsoft Public
License.

## SnoopWPF

- Project: SnoopWPF
- Source: https://github.com/snoopwpf/snoopwpf
- License: Microsoft Public License, Ms-PL

The source checkout pins Snoop as the `references/snoopwpf` git submodule. Its
license text is stored at `references/snoopwpf/License.txt` after the submodule
is initialized.

Packaged output includes the same license in the server's `snoop/` directory.
The NuGet package also includes it at
`THIRD_PARTY_LICENSES/Snoop-Ms-PL.txt`.

## FlaUI

- Packages: `FlaUI.Core` 5.0.0 and `FlaUI.UIA3` 5.0.0
- Source: https://github.com/FlaUI/FlaUI
- License: MIT

FlaUI provides the out-of-process UI Automation backend.

## Model Context Protocol C# SDK

- Package: `ModelContextProtocol` 1.4.1
- Source: https://github.com/modelcontextprotocol/csharp-sdk
- License: Apache-2.0

The SDK provides the MCP host, stdio transport, protocol types, and tool
registration support.

## Microsoft.Extensions.Hosting

- Package: `Microsoft.Extensions.Hosting` 10.0.3
- Source: https://github.com/dotnet/dotnet
- License: MIT

This package provides the server's host, dependency injection, configuration,
and logging infrastructure.

The tool bundles its managed runtime dependencies into the server payload
instead of declaring them as dependencies of the global-tool package. This
notice identifies the direct runtime packages. Their linked repositories carry
the full license texts and notices for their own dependencies.
