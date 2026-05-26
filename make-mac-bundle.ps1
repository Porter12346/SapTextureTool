param([string]$PublishDir = "publish-mac", [string]$OutZip = "SapTextureTool-mac.zip")

$publishDir = Join-Path $PSScriptRoot $PublishDir
$outZip     = Join-Path $PSScriptRoot $OutZip

if (Test-Path $outZip) { Remove-Item $outZip }

Add-Type -AssemblyName System.IO.Compression

$stream  = [System.IO.File]::Open($outZip, [System.IO.FileMode]::Create)
$archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create)

function Add-Entry($archive, $entryName, [byte[]]$bytes, [bool]$exec) {
    $e = $archive.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
    # Unix external attributes: high 16 bits = mode. 0x81ED = regular file, rwxr-xr-x (755)
    #                                                  0x81A4 = regular file, rw-r--r-- (644)
    $e.ExternalAttributes = if ($exec) { 0x81ED -shl 16 } else { 0x81A4 -shl 16 }
    $s = $e.Open()
    $s.Write($bytes, 0, $bytes.Length)
    $s.Close()
}

function Add-File($archive, $src, $entryName, [bool]$exec) {
    Add-Entry $archive $entryName ([System.IO.File]::ReadAllBytes($src)) $exec
}

$infoPlist = @'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>    <string>SapTextureTool</string>
    <key>CFBundleIdentifier</key>   <string>com.saptexturetool</string>
    <key>CFBundleName</key>         <string>SAP Texture Tool</string>
    <key>CFBundlePackageType</key>  <string>APPL</string>
    <key>CFBundleVersion</key>      <string>1.0</string>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSPrincipalClass</key>     <string>NSApplication</string>
</dict>
</plist>
'@

$root = "SapTextureTool.app/Contents"

Add-Entry $archive "$root/Info.plist" ([System.Text.Encoding]::UTF8.GetBytes($infoPlist)) $false

Add-File $archive "$publishDir\SapTextureTool"          "$root/MacOS/SapTextureTool"          $true
Add-File $archive "$publishDir\classdata.tpk"           "$root/MacOS/classdata.tpk"           $false
Add-File $archive "$publishDir\libSkiaSharp.dylib"       "$root/MacOS/libSkiaSharp.dylib"       $true
Add-File $archive "$publishDir\libHarfBuzzSharp.dylib"   "$root/MacOS/libHarfBuzzSharp.dylib"   $true
Add-File $archive "$publishDir\libAvaloniaNative.dylib"  "$root/MacOS/libAvaloniaNative.dylib"  $true

$archive.Dispose()
$stream.Dispose()

Write-Host "Created: $outZip"
