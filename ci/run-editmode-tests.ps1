<#
.SYNOPSIS
  Runs the NovaGames SDK EditMode tests with Unity in batchmode (CI, or locally while the Editor is closed).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File ci/run-editmode-tests.ps1
  powershell -ExecutionPolicy Bypass -File ci/run-editmode-tests.ps1 -UnityPath "D:\Unity\6000.0.68f1\Editor\Unity.exe"

.NOTES
  Exit code: 0 = all tests passed, 2 = some tests failed,
             3 = could not run (Unity not found, project open in the Editor, compile error, license, crash).
  Results: TestResults/editmode-results.xml (NUnit 3 XML) and TestResults/editmode.log (Unity log).
#>
param(
    [string]$ProjectPath = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$UnityPath = '',
    [string]$ResultsDir = '',
    # Semicolon-separated test assemblies. Default: every *.Tests.Editor asmdef under Packages/com.novagames.sdk/Tests.
    [string]$AssemblyNames = ''
)

$ErrorActionPreference = 'Stop'

function Fail([string]$message) {
    Write-Host "ERROR: $message" -ForegroundColor Red
    exit 3
}

if (-not $ResultsDir) { $ResultsDir = Join-Path $ProjectPath 'TestResults' }
New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null
$results = Join-Path $ResultsDir 'editmode-results.xml'
$log = Join-Path $ResultsDir 'editmode.log'
if (Test-Path $results) { Remove-Item $results -Force }

# 1. Unity: the exact version in ProjectSettings/ProjectVersion.txt, installed by Unity Hub.
if (-not $UnityPath) {
    $versionFile = Join-Path $ProjectPath 'ProjectSettings/ProjectVersion.txt'
    $match = Select-String -Path $versionFile -Pattern '^m_EditorVersion:\s*(\S+)'
    if (-not $match) { Fail "Cannot read the Unity version from $versionFile" }
    $version = $match.Matches[0].Groups[1].Value
    $roots = @($env:UNITY_EDITORS_DIR, (Join-Path $env:ProgramFiles 'Unity\Hub\Editor')) | Where-Object { $_ }
    foreach ($root in $roots) {
        $candidate = Join-Path $root "$version\Editor\Unity.exe"
        if (Test-Path $candidate) { $UnityPath = $candidate; break }
    }
    if (-not $UnityPath) { Fail "Unity $version not found. Install it with Unity Hub, set UNITY_EDITORS_DIR or pass -UnityPath." }
}
if (-not (Test-Path $UnityPath)) { Fail "Unity not found at $UnityPath" }

# 2. Unity refuses to open a project that is already open in an Editor (Temp/UnityLockfile is held).
$lockFile = Join-Path $ProjectPath 'Temp/UnityLockfile'
if (Test-Path $lockFile) {
    try {
        $stream = [System.IO.File]::Open($lockFile, 'Open', 'ReadWrite', 'None')
        $stream.Close()
    }
    catch {
        Fail "The project is open in the Unity Editor. Close it, or run the tests from Window > General > Test Runner."
    }
}

# 3. SDK test assemblies only (vendor plugins ship their own tests).
if (-not $AssemblyNames) {
    $testsDir = Join-Path $ProjectPath 'Packages/com.novagames.sdk/Tests'
    $AssemblyNames = (Get-ChildItem -Path $testsDir -Recurse -Filter '*.Tests.Editor.asmdef' |
        ForEach-Object { $_.BaseName }) -join ';'
    if (-not $AssemblyNames) { Fail "No *.Tests.Editor.asmdef found under $testsDir" }
}

Write-Host "Unity:      $UnityPath"
Write-Host "Project:    $ProjectPath"
Write-Host "Assemblies: $AssemblyNames"

# -runTests quits Unity by itself when the run finishes (do not add -quit).
$arguments = @(
    '-batchmode', '-nographics',
    '-projectPath', "`"$ProjectPath`"",
    '-runTests', '-testPlatform', 'EditMode',
    '-assemblyNames', "`"$AssemblyNames`"",
    '-testResults', "`"$results`"",
    '-logFile', "`"$log`""
)
$process = Start-Process -FilePath $UnityPath -ArgumentList $arguments -Wait -PassThru -NoNewWindow
$unityExit = $process.ExitCode

if (-not (Test-Path $results)) {
    if (Test-Path $log) {
        Write-Host '--- Last lines of the Unity log ---'
        Get-Content $log -Tail 40 | ForEach-Object { Write-Host $_ }
    }
    Fail "Unity exited with code $unityExit and wrote no test results (compile error, license or crash). Full log: $log"
}

# 4. Summary + failed tests from the NUnit 3 XML.
[xml]$xml = Get-Content -Path $results -Raw -Encoding UTF8
$run = $xml.'test-run'
Write-Host ("EditMode: {0} total, {1} passed, {2} failed, {3} skipped" -f $run.total, $run.passed, $run.failed, $run.skipped)
foreach ($case in $xml.SelectNodes("//test-case[@result='Failed']")) {
    Write-Host ("FAIL " + $case.fullname) -ForegroundColor Red
    $message = $case.SelectSingleNode('failure/message')
    if ($message) { Write-Host ("     " + $message.InnerText.Trim()) }
}

if ([int]$run.failed -gt 0) { exit 2 }
if ($unityExit -ne 0) { Fail "Unity exited with code $unityExit. Full log: $log" }
exit 0
