$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'azure-app'
$archive = Join-Path $artifacts 'ffmpeg-n9.0-linux64-gpl.tar.xz'
$expected = 'B28D9AF79527302004B380A69B3D78EDFFC9CCABBF23F70D963945C744BC7EC2'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -UseBasicParsing `
        -Uri 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-linux64-gpl-9.0.tar.xz' `
        -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) {
    throw 'FFmpeg archive does not match the reviewed build. Review a new checksum rather than bypassing validation.'
}
dotnet publish (Join-Path $root 'VideoTranslator\VideoTranslator.csproj') -c Release -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
$tools = Join-Path $publish 'tools'
New-Item -ItemType Directory -Path $tools -Force | Out-Null
tar.exe -xf $archive -C $tools --strip-components=1
if ($LASTEXITCODE -ne 0) { throw 'FFmpeg archive extraction failed.' }
foreach ($binary in @('ffmpeg', 'ffprobe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $tools "bin\$binary"))) {
        throw "Required Linux binary is missing: $binary"
    }
}
$startup = Join-Path $publish 'startup.sh'
$text = [IO.File]::ReadAllText($startup).Replace("`r`n", "`n")
[IO.File]::WriteAllText($startup, $text, [Text.UTF8Encoding]::new($false))
$zip = Join-Path $artifacts 'azure-app.zip'
$temporaryZip = "$zip.tmp"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
if (Test-Path -LiteralPath $temporaryZip) { Remove-Item -LiteralPath $temporaryZip }
$package = [IO.Compression.ZipFile]::Open($temporaryZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $publish -File -Recurse -Force) {
        # Windows PowerShell Compress-Archive emits backslashes that Linux ZIP mounts cannot resolve.
        $entryName = $file.FullName.Substring($publish.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $package, $file.FullName, $entryName, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally {
    $package.Dispose()
}
$package = [IO.Compression.ZipFile]::OpenRead($temporaryZip)
try {
    foreach ($required in @('VideoTranslator.dll', 'startup.sh', 'tools/bin/ffmpeg', 'tools/bin/ffprobe', 'tools/LICENSE.txt')) {
        $entry = $package.GetEntry($required)
        if ($null -eq $entry -or $entry.Length -eq 0) { throw "Deployment ZIP is missing: $required" }
    }
    if ($package.Entries | Where-Object { $_.FullName.Contains('\') }) {
        throw 'Deployment ZIP contains non-portable path separators.'
    }
} finally {
    $package.Dispose()
}
Move-Item -LiteralPath $temporaryZip -Destination $zip -Force
Write-Output "Prepared deployment package: $zip"
