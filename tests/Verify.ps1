$ErrorActionPreference = 'Stop'
$memoWorkspace = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $memoWorkspace
try {
    & dotnet run --project tests/MemoLite.Core.Tests/MemoLite.Core.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    & dotnet build src/MemoLite/MemoLite.csproj -c Debug -p:Platform=x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Debug build failed.' }
    $memoReportFolder = Join-Path $memoWorkspace 'artifacts\verification'
    New-Item -ItemType Directory -Force -Path $memoReportFolder | Out-Null
    $memoReport = Join-Path $memoReportFolder ('ui-smoke-' + [guid]::NewGuid().ToString('N') + '.txt')
    $memoExecutable = Join-Path $memoWorkspace 'src\MemoLite\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\MemoLite.exe'
    $memoProcess = Start-Process -FilePath $memoExecutable -ArgumentList '--smoke-test',('"' + $memoReport + '"') -WorkingDirectory $memoWorkspace -WindowStyle Hidden -PassThru
    if (-not $memoProcess.WaitForExit(45000)) { throw 'WinUI verification timed out.' }
    if (-not (Test-Path -LiteralPath $memoReport)) { throw 'WinUI verification did not produce a report.' }
    $memoReportText = Get-Content -LiteralPath $memoReport -Encoding utf8
    $memoReportText | Write-Output
    if ($memoReportText -match '^FAIL' -or -not ($memoReportText -match '^SUCCESS')) { throw 'WinUI verification failed.' }
    # A real close followed by a second process verifies recovery independently
    # of the in-memory editor and keeps all fixtures away from the user's tabs.
    $memoSessionReport = Join-Path $memoReportFolder ('session-lifecycle-' + [guid]::NewGuid().ToString('N') + '.txt')
    foreach ($memoPhase in @('--session-write-test', '--session-read-test')) {
        $memoSessionProcess = Start-Process -FilePath $memoExecutable -ArgumentList $memoPhase,('"' + $memoSessionReport + '"') -WorkingDirectory $memoWorkspace -WindowStyle Hidden -PassThru
        if (-not $memoSessionProcess.WaitForExit(45000)) { throw ('Session verification timed out: ' + $memoPhase) }
        if (-not (Test-Path -LiteralPath $memoSessionReport)) { throw 'Session verification did not produce a report.' }
        if ((Get-Content -LiteralPath $memoSessionReport -Encoding utf8) -match '^FAIL') { throw 'Session lifecycle verification failed.' }
    }
    $memoSessionText = Get-Content -LiteralPath $memoSessionReport -Encoding utf8
    $memoSessionText | Write-Output
    if (-not ($memoSessionText -match '^SUCCESS')) { throw 'Session lifecycle verification did not finish.' }
    # Check the actual Release publish output as well as the Debug controls.
    $memoPublishFolder = Join-Path $memoReportFolder ('publish-' + [guid]::NewGuid().ToString('N'))
    & dotnet publish src/MemoLite/MemoLite.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o $memoPublishFolder --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }
    foreach ($memoRequiredFile in @('MemoLite.exe', 'App.xbf', 'MainWindow.xbf', 'MemoLite.pri')) {
        if (-not (Test-Path -LiteralPath (Join-Path $memoPublishFolder $memoRequiredFile))) {
            throw ('Missing published resource: ' + $memoRequiredFile)
        }
    }
    Write-Output 'PASS Release publish contains the executable and compiled XAML resources.'
} finally {
    Pop-Location
}
