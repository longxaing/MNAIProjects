$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
$backendRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$fixtureRoot = Join-Path $backendRoot "tests/BuildWorkerFixture"
$excluded = @("node_modules", "bin", "obj", "dist", "TestResults", "test-results", "playwright-report", ".git")

$memory = [System.IO.MemoryStream]::new()
$archive = [System.IO.Compression.ZipArchive]::new(
    $memory,
    [System.IO.Compression.ZipArchiveMode]::Create,
    $true)
try {
    Get-ChildItem $fixtureRoot -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($fixtureRoot.Length + 1).Replace("\", "/")
        if (($relative -split "/" | Where-Object { $excluded -contains $_ }).Count -eq 0) {
            $entry = $archive.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
            $source = [System.IO.File]::OpenRead($_.FullName)
            $target = $entry.Open()
            try { $source.CopyTo($target) } finally { $target.Dispose(); $source.Dispose() }
        }
    }
} finally {
    $archive.Dispose()
}

$payload = @{
    sourceArchiveBase64 = [Convert]::ToBase64String($memory.ToArray())
    solutionPath = "GeneratedApp.sln"
    backendProjectPath = "src/backend/GeneratedApp.Api.csproj"
    frontendDirectory = "src/frontend"
    commandTimeoutMinutes = 10
    totalTimeoutMinutes = 30
    playwrightVersion = "1.62.1"
} | ConvertTo-Json -Compress
$response = Invoke-RestMethod -Uri "http://127.0.0.1:3000/build" -Method Post `
    -ContentType "application/json" -Body $payload -TimeoutSec 1800
if (-not $response.succeeded -or $response.steps.Count -ne 9) {
    $response.steps | ForEach-Object {
        Write-Output "[$($_.name)] exit=$($_.exitCode)"
        Write-Output $_.output
    }
    throw "Sandbox build failed: $($response.summary)"
}

$desktop = [Convert]::FromBase64String($response.desktopScreenshotBase64)
$mobile = [Convert]::FromBase64String($response.mobileScreenshotBase64)
foreach ($image in @($desktop, $mobile)) {
    if ($image.Length -lt 1000 -or $image[0] -ne 137 -or $image[1] -ne 80 -or $image[2] -ne 78 -or $image[3] -ne 71) {
        throw "Sandbox screenshot is not a valid PNG."
    }
}

$response.steps | Select-Object name, succeeded, exitCode, durationMilliseconds
Write-Output "Desktop screenshot: $($desktop.Length) bytes"
Write-Output "Mobile screenshot: $($mobile.Length) bytes"