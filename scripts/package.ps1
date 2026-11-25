param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$Csproj = Join-Path $RepoRoot "RS8Control\RS8Control.csproj"
$DistDir = Join-Path $RepoRoot "dist"
$StageNet10 = Join-Path $DistDir "stage\net10"
$StageStandalone = Join-Path $DistDir "stage\standalone"

if (-not (Test-Path $Csproj)) {
    throw "未找到工程文件: $Csproj"
}

$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnetCmd) {
    $DotnetExe = $dotnetCmd.Source
}
else {
    $fallback = "C:\Program Files\dotnet\dotnet.exe"
    if (Test-Path $fallback) { $DotnetExe = $fallback }
    else { throw "未找到 dotnet CLI，请先安装 .NET 10 SDK。" }
}

if (Test-Path $DistDir) {
    Remove-Item -Recurse -Force $DistDir
}
New-Item -ItemType Directory -Force -Path $StageNet10, $StageStandalone | Out-Null

function Copy-CommonDocs([string]$TargetDir) {
    foreach ($name in @("README.md", "LICENSE")) {
        $src = Join-Path $RepoRoot $name
        if (Test-Path $src) {
            Copy-Item $src (Join-Path $TargetDir $name) -Force
        }
    }
}

Write-Host "[1/4] 发布轻量便携版 (win-x64, 需 .NET 10 Desktop Runtime)..."
& $DotnetExe publish $Csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o $StageNet10
if ($LASTEXITCODE -ne 0) { throw "轻量版 dotnet publish 失败 (exit $LASTEXITCODE)" }
Copy-CommonDocs $StageNet10

Write-Host "[2/4] 发布免依赖单文件独立版 (win-x64 self-contained)..."
& $DotnetExe publish $Csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $StageStandalone
if ($LASTEXITCODE -ne 0) { throw "独立版 dotnet publish 失败 (exit $LASTEXITCODE)" }
Copy-CommonDocs $StageStandalone

$Net10Zip = Join-Path $DistDir "RS8Control-v$Version-win-x64-net10.zip"
$StandaloneZip = Join-Path $DistDir "RS8Control-v$Version-win-x64-standalone.zip"
$SumsPath = Join-Path $DistDir "SHA256SUMS.txt"

Write-Host "[3/4] 压缩打包到 dist/ ..."
Compress-Archive -Path (Join-Path $StageNet10 "*") -DestinationPath $Net10Zip -CompressionLevel Optimal -Force
Compress-Archive -Path (Join-Path $StageStandalone "*") -DestinationPath $StandaloneZip -CompressionLevel Optimal -Force

Remove-Item -Recurse -Force (Join-Path $DistDir "stage")

Write-Host "[4/4] 生成 SHA256SUMS.txt ..."
$hashes = foreach ($zip in @($StandaloneZip, $Net10Zip)) {
    $h = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$h  $(Split-Path $zip -Leaf)"
}
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllLines($SumsPath, $hashes, $utf8NoBom)

Write-Host ""
Write-Host "打包完成:"
Get-ChildItem $DistDir | Format-Table Name, @{Label="Size(KB)"; Expression={[math]::Round($_.Length / 1KB, 1)}}, LastWriteTime -AutoSize
Write-Host "SHA-256:"
$hashes | ForEach-Object { Write-Host "  $_" }
