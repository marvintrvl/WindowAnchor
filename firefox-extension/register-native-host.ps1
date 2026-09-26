param(
    [Parameter(Mandatory = $true)]
    [string]$WindowAnchorPath
)

$resolvedExecutable = (Resolve-Path -LiteralPath $WindowAnchorPath).Path
$hostDirectory = Join-Path $env:LOCALAPPDATA 'WindowAnchor'
New-Item -ItemType Directory -Path $hostDirectory -Force | Out-Null
$manifestPath = Join-Path $hostDirectory 'native-host-manifest-firefox.json'
$manifest = [ordered]@{
    name = 'com.windowanchor.browser'
    description = 'WindowAnchor Firefox session native messaging host'
    path = $resolvedExecutable
    type = 'stdio'
    allowed_extensions = @('windowanchor-browser-connector@windowanchor.app')
}
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

$location = 'HKCU:\Software\Mozilla\NativeMessagingHosts\com.windowanchor.browser'
New-Item -Path $location -Force | Out-Null
Set-ItemProperty -Path $location -Name '(default)' -Value $manifestPath

Write-Output "Registered the WindowAnchor native host for Firefox at $manifestPath."
