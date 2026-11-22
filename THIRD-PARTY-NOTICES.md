# Third-party notices

Seekbeam is licensed under the MIT License (see `LICENSE`). The Seekbeam installer and portable package also redistribute
the following third-party components. Each one remains under its own license.

| Component | Version | License | Project |
|---|---|---|---|
| .NET runtime and WPF (self-contained deployment) | 10.0 | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf |
| Microsoft.Data.Sqlite | 10.0.15 | MIT | https://github.com/dotnet/efcore |
| SQLitePCLRaw (bundle_e_sqlite3) | 3.0.5 | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLite (e_sqlite3 native library) | bundled with SQLitePCLRaw | Public domain | https://sqlite.org/copyright.html |
| PdfPig | 0.1.16 | Apache-2.0 | https://github.com/UglyToad/PdfPig |

Build and test tools that are **not** redistributed: xUnit (Apache-2.0), Microsoft.NET.Test.Sdk (MIT),
Microsoft.CodeAnalysis.BannedApiAnalyzers (MIT), WiX Toolset (MS-RL; since v6 also subject to the Open Source Maintenance Fee EULA).

The full text of the Apache License 2.0 is available at https://www.apache.org/licenses/LICENSE-2.0, and the text of
the MIT License used by the .NET components at https://github.com/dotnet/runtime/blob/main/LICENSE.TXT.
Neither SQLitePCLRaw nor PdfPig ships a NOTICE file that requires additional attribution at the time of writing;
check again when upgrading these packages.
