param(
    [ValidatePattern('^[a-p]{32}$')]
    [string]$ExtensionId = 'iclppjbhmhkpinkednfkokinenijlalh',

    [string]$ExecutablePath = (Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\CategorizedDownload.exe')
)

$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath -ErrorAction Stop).Path
$hostDirectory = Join-Path $env:LOCALAPPDATA 'CategorizedDownload'
$manifestPath = Join-Path $hostDirectory 'chrome-host.json'
New-Item -ItemType Directory -Path $hostDirectory -Force | Out-Null

$manifest = @{
    name = 'com.categorizeddownload.host'
    description = 'Categorized Download Chrome bridge'
    path = $resolvedExecutable
    type = 'stdio'
    allowed_origins = @("chrome-extension://$ExtensionId/")
} | ConvertTo-Json
[System.IO.File]::WriteAllText($manifestPath, $manifest, [System.Text.UTF8Encoding]::new($false))

$registryPaths = @(
    'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.categorizeddownload.host'
    'HKCU:\Software\Chromium\NativeMessagingHosts\com.categorizeddownload.host'
)
foreach ($registryPath in $registryPaths) {
    New-Item -Path $registryPath -Force | Out-Null
    Set-Item -Path $registryPath -Value $manifestPath
}

foreach ($view in @('32', '64')) {
    & reg.exe add 'HKCU\Software\Google\Chrome\NativeMessagingHosts\com.categorizeddownload.host' /ve /t REG_SZ /d $manifestPath /f "/reg:$view" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Chrome native host registration failed for the $view-bit registry view."
    }
}
Write-Host "Chrome native host registered for extension $ExtensionId"
