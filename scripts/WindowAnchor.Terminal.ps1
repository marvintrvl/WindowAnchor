# Optional Windows Terminal / PowerShell integration. Dot-source this file at the END of a
# PowerShell profile. It never installs itself and is inert outside Windows Terminal.
$parsedSession = [guid]::Empty
$parsedProfile = [guid]::Empty
if ($env:WT_SESSION -and $env:WT_PROFILE_ID -and
    [guid]::TryParse($env:WT_SESSION, [ref]$parsedSession) -and
    [guid]::TryParse($env:WT_PROFILE_ID, [ref]$parsedProfile)) {
    if (-not $global:WindowAnchorPreviousPrompt) {
        $global:WindowAnchorPreviousPrompt = (Get-Command prompt).ScriptBlock
    }

    function global:prompt {
        try {
            $session = [guid]$env:WT_SESSION
            $profile = [guid]$env:WT_PROFILE_ID
            $location = Get-Location
            if ($location.Provider.Name -eq 'FileSystem') {
                $directory = Join-Path $env:LOCALAPPDATA 'WindowAnchor\TerminalSessions'
                [System.IO.Directory]::CreateDirectory($directory) | Out-Null
                $file = Join-Path $directory ($session.ToString('N') + '.json')
                $report = @{
                    sessionId = $session.ToString('D')
                    profileId = $profile.ToString('D')
                    directory = $location.Path
                    updatedAtUtc = [DateTime]::UtcNow.ToString('o')
                } | ConvertTo-Json -Compress
                [System.IO.File]::WriteAllText($file, $report)
            }

            # Terminal exposes tab titles to accessibility, but not WT_SESSION. This short,
            # visible marker lets WindowAnchor associate this prompt report with its tab.
            $marker = '[WA:' + $session.ToString('N').Substring(0, 12) + ']'
            $title = $Host.UI.RawUI.WindowTitle -replace '\s*\[WA:[0-9a-fA-F]{12}\]', ''
            $Host.UI.RawUI.WindowTitle = ($title.TrimEnd() + ' ' + $marker).Trim()
        } catch {
            # A prompt must remain usable even if reporting or title updates fail.
        }
        & $global:WindowAnchorPreviousPrompt
    }
}
