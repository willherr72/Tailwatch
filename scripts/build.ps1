param([string]$Output = (Join-Path $PSScriptRoot '../dist/windows'))
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    cargo test --manifest-path agent/Cargo.toml --locked
    if($LASTEXITCODE -ne 0){throw 'Agent tests failed'}
    dotnet test tests/Tailwatch.Tests --nologo
    if($LASTEXITCODE -ne 0){throw 'Viewer tests failed'}
    cargo build --manifest-path agent/Cargo.toml --release --locked
    if($LASTEXITCODE -ne 0){throw 'Agent build failed'}
    dotnet publish viewer/Tailwatch.App -c Release -r win-x64 --self-contained true -o $Output -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
    if($LASTEXITCODE -ne 0){throw 'Viewer build failed'}
    $target=if($env:CARGO_TARGET_DIR){$env:CARGO_TARGET_DIR}else{Join-Path $repo 'agent/target'}
    Copy-Item -LiteralPath (Join-Path $target 'release/tailwatch-agent.exe') -Destination $Output
    Copy-Item -LiteralPath (Join-Path $repo 'README.md'),(Join-Path $repo 'LICENSE'),(Join-Path $PSScriptRoot 'install-windows.ps1'),(Join-Path $PSScriptRoot 'uninstall-windows.ps1') -Destination $Output
    Write-Output "Built $Output. Run Tailwatch.exe for the viewer; service installation is only needed for remote monitoring."
} finally {Pop-Location}
