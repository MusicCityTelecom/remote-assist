# Build validation

`main` is validated by GitHub Actions on Ubuntu for the Go broker/API and on Windows for the self-contained .NET 8 customer agent. A successful main build publishes the rolling prerelease asset `Remote-Assist.exe` under the `preview-latest` tag.
