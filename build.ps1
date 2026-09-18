param([switch]$Test, [switch]$InteractiveTests, [switch]$Package)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The .NET Framework C# compiler is required. Build on Windows with .NET Framework 4.x.'
}
$bin = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null
$exe = Join-Path $bin 'QuickFrame.exe'
& $compiler /nologo /target:winexe /optimize+ /platform:anycpu "/out:$exe" "/win32manifest:$PSScriptRoot\src\app.manifest" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll (Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src\App.config') -Destination "$exe.config" -Force
if ($Test -or $InteractiveTests) {
    $checks = @('--self-test', '--settings-test')
    if ($InteractiveTests) { $checks += @('--smoke-test', '--worker-test', '--ui-test') }
    foreach ($check in $checks) {
        $process = Start-Process -FilePath $exe -ArgumentList $check -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(30000)) {
            Stop-Process -Id $process.Id
            throw "Test timed out: $check"
        }
        $process.Refresh()
        if ($process.ExitCode -ne 0) { throw "Test failed: $check. Inspect bin/*-test.txt." }
        Write-Output "PASS $check"
    }
}
if ($Package) {
    $dist = Join-Path $PSScriptRoot 'dist'
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    $files = @($exe, "$exe.config", (Join-Path $PSScriptRoot 'LICENSE'), (Join-Path $PSScriptRoot 'README.md'), (Join-Path $PSScriptRoot 'docs\usage.zh-CN.md'))
    $archive = Join-Path $dist 'QuickFrame-windows.zip'
    Compress-Archive -LiteralPath $files -DestinationPath $archive -Force
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Value "$hash  QuickFrame-windows.zip" -Encoding ascii
    Write-Output "Package: $archive"
}
Write-Output "Build: $exe"
