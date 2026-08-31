param()

$ErrorActionPreference = "Stop"

$edgeUrl = $env:VME_EDGE_URL
if ([string]::IsNullOrWhiteSpace($edgeUrl)) {
    $edgeUrl = "http://127.0.0.1:7338"
}

$hookToken = $env:VME_EDGE_HOOK_TOKEN
if ([string]::IsNullOrWhiteSpace($hookToken)) {
    [Console]::Error.WriteLine("vNext Memory hook skipped: VME_EDGE_HOOK_TOKEN is not configured.")
    exit 0
}

$payload = [Console]::In.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($payload)) {
    exit 0
}

$headers = @{
    "X-VME-Hook-Token" = $hookToken
}

$scopeHeaders = @{
    "X-VME-Project" = $env:VME_PROJECT_ID
    "X-VME-Repository" = $env:VME_REPOSITORY_ID
    "X-VME-Branch" = $env:VME_BRANCH
    "X-VME-Worktree" = $env:VME_WORKTREE_ID
    "X-VME-Environment" = $env:VME_ENVIRONMENT
    "X-VME-Task" = $env:VME_TASK_ID
}

foreach ($entry in $scopeHeaders.GetEnumerator()) {
    if (-not [string]::IsNullOrWhiteSpace($entry.Value)) {
        $headers[$entry.Key] = $entry.Value
    }
}

try {
    Invoke-RestMethod `
        -Method Post `
        -Uri "$($edgeUrl.TrimEnd('/'))/api/v1/hooks/claude-code" `
        -Headers $headers `
        -ContentType "application/json" `
        -Body $payload `
        -TimeoutSec 10 | Out-Null
} catch {
    # Memory capture must fail open and must never block the coding session.
    [Console]::Error.WriteLine("vNext Memory hook failed: $($_.Exception.Message)")
}

exit 0
