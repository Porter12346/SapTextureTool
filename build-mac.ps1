param(
    [string[]]$Arch = @("x64", "arm64")
)

$AppName    = "SapTextureTool"
$BundleId   = "com.saptexturetool.app"
$DisplayName = "SAP Texture Tool"
$OutDir     = "bin\mac-release"

$InfoPlist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>$AppName</string>
    <key>CFBundleDisplayName</key>
    <string>$DisplayName</string>
    <key>CFBundleIdentifier</key>
    <string>$BundleId</string>
    <key>CFBundleVersion</key>
    <string>1.0.0</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0</string>
    <key>CFBundleExecutable</key>
    <string>$AppName</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
    <key>LSMinimumSystemVersion</key>
    <string>10.14</string>
</dict>
</plist>
"@

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# .NET's ZipArchive hard-codes "version made by" as 0 (MS-DOS) in every central
# directory entry. macOS unzip only applies Unix ExternalAttributes when that byte
# is 3 (Unix). This function patches byte 5 of each central directory record to 3.
function Set-ZipUnixMadeBy {
    param([string]$Path)
    $data = [System.IO.File]::ReadAllBytes($Path)
    # Locate End of Central Directory record (signature 50 4B 05 06) from near end
    for ($i = $data.Length - 22; $i -ge 0; $i--) {
        if ($data[$i] -eq 0x50 -and $data[$i+1] -eq 0x4B -and
            $data[$i+2] -eq 0x05 -and $data[$i+3] -eq 0x06) {
            $cdOffset = [BitConverter]::ToUInt32($data, $i + 16)
            $cdCount  = [BitConverter]::ToUInt16($data, $i + 10)
            $pos = [int]$cdOffset
            for ($j = 0; $j -lt $cdCount; $j++) {
                # Verify central directory entry signature: 50 4B 01 02
                if ($data[$pos]   -ne 0x50 -or $data[$pos+1] -ne 0x4B -or
                    $data[$pos+2] -ne 0x01 -or $data[$pos+3] -ne 0x02) { break }
                $data[$pos + 5] = 3   # "made by" system: 3 = Unix
                $fnLen = [BitConverter]::ToUInt16($data, $pos + 28)
                $exLen = [BitConverter]::ToUInt16($data, $pos + 30)
                $cmLen = [BitConverter]::ToUInt16($data, $pos + 32)
                $pos  += 46 + $fnLen + $exLen + $cmLen
            }
            break
        }
    }
    [System.IO.File]::WriteAllBytes($Path, $data)
}

function New-MacZip {
    param([string]$AppBundlePath, [string]$ZipPath)

    # Unix permission constants (stored in high 16 bits of ExternalAttributes)
    $UnixExec = (0x81ED -shl 16)   # -rwxr-xr-x  regular file, executable
    $UnixFile = (0x81A4 -shl 16)   # -rw-r--r--  regular file
    $UnixDir  = (0x41ED -shl 16)   # drwxr-xr-x  directory

    # Executables: the main binary and all .dylib files
    $execExts = @('.dylib')
    $execNames = @($AppName)

    Remove-Item $ZipPath -ErrorAction SilentlyContinue
    $fs  = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::Create)
    $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create, $false)

    try {
        # Root is the folder that CONTAINS SapTextureTool.app, so entries start with "SapTextureTool.app/..."
        $root = (Resolve-Path (Split-Path $AppBundlePath -Parent)).Path

        Get-ChildItem -LiteralPath $AppBundlePath -Recurse | ForEach-Object {
            $rel = $_.FullName.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')

            if ($_.PSIsContainer) {
                $entry = $zip.CreateEntry("$rel/", [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.ExternalAttributes = $UnixDir
            } else {
                $isExec = ($execNames -contains $_.BaseName -and $_.Extension -eq '') -or
                          ($execExts -contains $_.Extension)
                $entry = $zip.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.ExternalAttributes = if ($isExec) { $UnixExec } else { $UnixFile }
                $es = $entry.Open()
                $bs = [System.IO.File]::OpenRead($_.FullName)
                $bs.CopyTo($es)
                $bs.Dispose(); $es.Dispose()
            }
        }
    } finally {
        $zip.Dispose()
        $fs.Dispose()
    }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

foreach ($arch in $Arch) {
    $rid        = "osx-$arch"
    $publishDir = "bin\publish-mac-$arch"
    $stageDir   = "$OutDir\$arch"
    $appBundle  = "$stageDir\$AppName.app"
    $macosDir   = "$appBundle\Contents\MacOS"

    Write-Host "Publishing $rid..." -ForegroundColor Cyan
    dotnet publish -c Release -r $rid --self-contained true -p:PublishSingleFile=true -o $publishDir
    if ($LASTEXITCODE -ne 0) { Write-Error "Publish failed for $rid"; continue }

    Write-Host "Building .app bundle for $rid..." -ForegroundColor Cyan
    Remove-Item -Recurse -Force $stageDir -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $macosDir | Out-Null

    # Copy all published files except .pdb into Contents/MacOS/
    Get-ChildItem $publishDir -File | Where-Object Extension -ne ".pdb" |
        Copy-Item -Destination $macosDir

    # Write Info.plist
    [System.IO.File]::WriteAllText("$appBundle\Contents\Info.plist", $InfoPlist, [System.Text.Encoding]::UTF8)

    # Create zip with correct structure and Unix permissions
    $zipPath = "$OutDir\$AppName-mac-$arch.zip"
    New-MacZip -AppBundlePath $appBundle -ZipPath $zipPath
    # Patch "version made by" to Unix so macOS unzip applies the permission bits
    Set-ZipUnixMadeBy -Path $zipPath
    Write-Host "Created $zipPath" -ForegroundColor Green
}
