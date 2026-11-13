[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [ValidateSet('win-arm64', 'win-x64', 'osx-arm64', 'osx-x64', 'linux-arm64', 'linux-x64')]
    [string[]] $Runtime = @('win-arm64', 'win-x64', 'osx-arm64', 'osx-x64', 'linux-arm64', 'linux-x64'),

    [ValidateNotNullOrEmpty()]
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

if (!$PSBoundParameters.ContainsKey('OutputPath')) {
    $OutputPath = Join-Path $PSScriptRoot 'artifacts'
}

$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
$projectPath = Join-Path $PSScriptRoot 'ChatGptYesterday.cs'
$outputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
$temporaryDirectory = [System.IO.Path]::GetFullPath((Join-Path ([System.IO.Path]::GetTempPath()) (
    'chatgpt-publish-' + [guid]::NewGuid().ToString('N'))))
[System.IO.Directory]::CreateDirectory($temporaryDirectory) | Out-Null
$failures = [System.Collections.Generic.List[string]]::new()

try {
    foreach ($targetRuntime in ($Runtime | Select-Object -Unique)) {
        try {
            $assemblyName = "ChatGptYesterday-$targetRuntime"
            $fileName = if ($targetRuntime.StartsWith('win-')) { "$assemblyName.exe" } else { $assemblyName }
            $publishPath = Join-Path $temporaryDirectory $targetRuntime
            $publishArguments = @(
                'publish', $projectPath,
                '--configuration', 'Release',
                '--runtime', $targetRuntime,
                '--self-contained', 'true',
                '--output', $publishPath,
                "-p:AssemblyName=$assemblyName",
                '-p:IncludeSourceRevisionInInformationalVersion=false',
                '-p:PublishAot=false',
                '-p:PublishTrimmed=true',
                '-p:PublishSingleFile=true',
                '-p:EnableCompressionInSingleFile=true',
                '-p:IncludeNativeLibrariesForSelfExtract=true',
                '-p:DebugType=embedded',
                "-p:PathMap=$PSScriptRoot=/_/src",
                '-p:GenerateDocumentationFile=false'
            )

            Write-Host "Publishing ChatGptYesterday for $targetRuntime..."
            & $dotnet @publishArguments
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet publish failed with exit code $LASTEXITCODE."
            }

            $executablePath = Join-Path $publishPath $fileName
            $entries = @(Get-ChildItem -LiteralPath $publishPath -Force)
            if ($entries.Count -ne 1 -or !(Test-Path -LiteralPath $executablePath -PathType Leaf)) {
                throw "Expected a single executable named $fileName; found: $($entries.Name -join ', ')."
            }

            [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
            $destination = Join-Path $outputDirectory $fileName
            Move-Item -LiteralPath $executablePath -Destination $destination -Force
            Write-Host "Published to: $destination"
        }
        catch {
            $failures.Add("${targetRuntime}: $($_.Exception.Message)")
            Write-Warning $failures[$failures.Count - 1]
        }
    }
}
finally {
    $temporaryParent = [System.IO.Path]::GetDirectoryName($temporaryDirectory)
    $expectedParent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    if ($temporaryParent -ne $expectedParent -or !(Split-Path -Leaf $temporaryDirectory).StartsWith('chatgpt-publish-')) {
        throw "Unexpected temporary directory: $temporaryDirectory"
    }
    Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
}

if ($failures.Count -gt 0) {
    throw "Publishing failed for $($failures.Count) runtime(s):`n$($failures -join "`n")"
}
