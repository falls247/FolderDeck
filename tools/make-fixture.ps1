<#
.SYNOPSIS
  Creates the FolderDeck verification folder tree (spec section 19.3).

.DESCRIPTION
  Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-fixture.ps1 -OutputDir E:\tmp\fixture

  The output folder must not exist yet (nothing is ever deleted or overwritten).
  Works on Windows PowerShell 5.1 and PowerShell 7. The source is ASCII only; non-ASCII
  names are built from \u escapes so the file reads the same under any code page.

  Contents (relative to OutputDir):
    nest8\            8 nested levels; every folder has 2 folders (L<n>_A, L<n>_B) and 3 files
    many_files\       10,000 files file_1.txt .. file_10000.txt (natural order check)
    many_folders\     5,000 folders folder_1 .. folder_5000
    long_path\        nested folders whose full path exceeds 260 characters (+ files, image)
    names\            Japanese, spaces, emoji, '&', '%' and a very long name
    attributes\       hidden, system, hidden+system, dot-file, dot-folder, hidden folder
    junction\loop     junction pointing at its own parent (cycle check)
    images\           with_image, no_image, nested_image, depth6_image (found),
                      deep_image (image only at depth 7: not found), broken_image
    executables\      test.bat (echo test) for the run confirmation
    empty\            empty folder
    access_denied\    read denied for the current user via icacls (revert command is printed)
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

function U([string]$text) { return [regex]::Unescape($text) }

$root = [System.IO.Path]::GetFullPath($OutputDir)
if (Test-Path -LiteralPath $root) {
    [Console]::Error.WriteLine("Output folder already exists, nothing was changed: $root")
    exit 1
}

$watch = [System.Diagnostics.Stopwatch]::StartNew()
$utf8 = New-Object System.Text.UTF8Encoding($false)
Add-Type -AssemblyName System.Drawing

if (-not ('FdLongPath' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class FdLongPath
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateDirectoryW(string lpPathName, IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    // Paths must carry the \\?\ prefix; works without the LongPathsEnabled policy.
    public static void CreateDirectory(string path)
    {
        if (!CreateDirectoryW(path, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 183) { throw new Win32Exception(error, path); }
        }
    }

    public static void WriteAllBytes(string path, byte[] data)
    {
        using (SafeFileHandle handle = CreateFileW(path, 0x40000000u, 0u, IntPtr.Zero, 1u, 0x80u, IntPtr.Zero))
        {
            if (handle.IsInvalid) { throw new Win32Exception(Marshal.GetLastWin32Error(), path); }
            using (FileStream stream = new FileStream(handle, FileAccess.Write))
            {
                stream.Write(data, 0, data.Length);
            }
        }
    }
}
'@
}

function J([string]$a, [string]$b) { return [System.IO.Path]::Combine($a, $b) }

function New-Dir([string]$path) {
    [void][System.IO.Directory]::CreateDirectory($path)
    return $path
}

function New-Text([string]$path, [string]$text) {
    if ([System.IO.File]::Exists($path)) { throw "Refusing to overwrite $path" }
    [System.IO.File]::WriteAllText($path, $text, $utf8)
}

function New-Bytes([string]$path, [byte[]]$bytes) {
    if ([System.IO.File]::Exists($path)) { throw "Refusing to overwrite $path" }
    [System.IO.File]::WriteAllBytes($path, $bytes)
}

function New-Png([int]$width, [int]$height, [int]$r, [int]$g, [int]$b, [string]$label) {
    $bmp = New-Object System.Drawing.Bitmap $width, $height
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $gfx.Clear([System.Drawing.Color]::FromArgb(255, $r, $g, $b))
    $gfx.FillRectangle([System.Drawing.Brushes]::White, 0, [int]($height * 0.7), $width, 4)
    $font = New-Object System.Drawing.Font 'Segoe UI', 11
    $gfx.DrawString($label, $font, [System.Drawing.Brushes]::White, 4, 4)
    $font.Dispose()
    $gfx.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

function Set-Attr([string]$path, [System.IO.FileAttributes]$add) {
    if ([System.IO.Directory]::Exists($path)) {
        $info = New-Object System.IO.DirectoryInfo $path
    } else {
        $info = New-Object System.IO.FileInfo $path
    }
    $info.Attributes = $info.Attributes -bor $add
}

$pdfBytes = $utf8.GetBytes("%PDF-1.4`n% FolderDeck dummy, not a real document`n%%EOF`n")
$xlsxBytes = [byte[]](0x50, 0x4B, 0x03, 0x04) + (New-Object byte[] 60)
$nameMemo = U '\u30E1\u30E2.txt'        # memo.txt in Japanese
$nameQuote = U '\u898B\u7A4D.pdf'       # estimate.pdf in Japanese
$nameList = U '\u4E00\u89A7.xlsx'       # list.xlsx in Japanese

New-Dir $root | Out-Null

# --- nest8: 8 nested levels, 2 folders + 3 files per folder ---------------------------------
function New-Nest([string]$dir, [int]$level) {
    New-Text (J $dir $nameMemo) "level $level"
    New-Bytes (J $dir $nameQuote) $pdfBytes
    New-Bytes (J $dir $nameList) $xlsxBytes
    if ($level -ge 8) { return }
    foreach ($suffix in @('A', 'B')) {
        $sub = New-Dir (J $dir ('L{0}_{1}' -f ($level + 1), $suffix))
        New-Nest $sub ($level + 1)
    }
}
$nest = New-Dir (J $root 'nest8')
New-Nest $nest 0

# --- many_files: 10,000 files -------------------------------------------------------------
$manyFiles = New-Dir (J $root 'many_files')
for ($i = 1; $i -le 10000; $i++) {
    [System.IO.File]::WriteAllText((J $manyFiles "file_$i.txt"), "$i", $utf8)
}

# --- many_folders: 5,000 folders ----------------------------------------------------------
$manyFolders = New-Dir (J $root 'many_folders')
for ($i = 1; $i -le 5000; $i++) {
    $d = J $manyFolders "folder_$i"
    [void][System.IO.Directory]::CreateDirectory($d)
    if ($i % 100 -eq 1) { [System.IO.File]::WriteAllText((J $d 'readme.txt'), "$i", $utf8) }
}

# --- long_path: full path longer than 260 characters --------------------------------------
$longBase = New-Dir (J $root 'long_path')
$current = $longBase
$segment = 0
$longDirs = New-Object System.Collections.ArrayList
while ($current.Length -le 300) {
    $segment++
    $current = J $current (('seg{0:D2}_' -f $segment) + ('x' * 45))
    [FdLongPath]::CreateDirectory('\\?\' + $current)
    [void]$longDirs.Add($current)
    [FdLongPath]::WriteAllBytes('\\?\' + (J $current 'note.txt'), $utf8.GetBytes("segment $segment"))
}
$longLeaf = $current
[FdLongPath]::WriteAllBytes('\\?\' + (J $longLeaf 'long_image.png'), (New-Png 96 128 46 125 50 'long'))
[FdLongPath]::WriteAllBytes('\\?\' + (J $longLeaf 'long_report.pdf'), $pdfBytes)
$longDeeper = J $longLeaf 'deeper'
[FdLongPath]::CreateDirectory('\\?\' + $longDeeper)
[FdLongPath]::WriteAllBytes('\\?\' + (J $longDeeper 'deep_note.txt'), $utf8.GetBytes('deeper'))

# --- names: Japanese, spaces, emoji, &, % --------------------------------------------------
$names = New-Dir (J $root 'names')
$jpFolder = New-Dir (J $names (U '\u65E5\u672C\u8A9E\u30D5\u30A9\u30EB\u30C0'))          # Japanese folder
New-Text (J $jpFolder (U '\u8CC7\u6599.txt')) 'jp'
$spaceFolder = New-Dir (J $names (U 'with space \u7A7A\u767D \u3092 \u542B\u3080'))
New-Text (J $spaceFolder 'file with spaces.txt') 'space'
$emojiFolder = New-Dir (J $names (U 'emoji \uD83D\uDE00 folder'))
New-Text (J $emojiFolder (U '\uD83C\uDF89 party.txt')) 'emoji'
$ampFolder = New-Dir (J $names 'A&B folder')
New-Text (J $ampFolder 'Tom & Jerry.txt') 'amp'
$pctFolder = New-Dir (J $names '100% done')
New-Text (J $pctFolder '50%.txt') 'pct'
$envFolder = New-Dir (J $names '%USERNAME% literal')
New-Text (J $envFolder 'not expanded.txt') 'env'
$longName = U '\u300C\u8CC7\u6599\u300D\u3002\u3068\u3066\u3082\u9577\u3044\u540D\u524D\u306E\u30D5\u30A1\u30A4\u30EB\u3067\u3059\u3001\u4E8C\u884C\u306B\u53CE\u307E\u3089\u306A\u3044\u3053\u3068\u3092\u78BA\u304B\u3081\u307E\u3059\uFF082026\u5E74\u5EA6\uFF09.txt'
New-Text (J $names $longName) 'long name'
New-Text (J $names 'brackets [a] (b) {c} #1.txt') 'brackets'

# --- attributes: hidden / system / dot names ----------------------------------------------
$attr = New-Dir (J $root 'attributes')
New-Text (J $attr 'visible.txt') 'visible'
New-Text (J $attr 'hidden.txt') 'hidden'
Set-Attr (J $attr 'hidden.txt') ([System.IO.FileAttributes]::Hidden)
New-Text (J $attr 'system.txt') 'system'
Set-Attr (J $attr 'system.txt') ([System.IO.FileAttributes]::System)
New-Text (J $attr 'hidden_system.txt') 'hidden+system'
Set-Attr (J $attr 'hidden_system.txt') ([System.IO.FileAttributes]'Hidden, System')
New-Text (J $attr '.dotfile') 'dot'
$dotFolder = New-Dir (J $attr '.dotfolder')
New-Text (J $dotFolder 'inside.txt') 'inside dot folder'
$hiddenFolder = New-Dir (J $attr 'hidden_folder')
New-Text (J $hiddenFolder 'inside.txt') 'inside hidden folder'
Set-Attr $hiddenFolder ([System.IO.FileAttributes]::Hidden)

# --- junction: points at its own parent ---------------------------------------------------
$junctionDir = New-Dir (J $root 'junction')
New-Text (J $junctionDir 'inside.txt') 'junction parent'
$loop = J $junctionDir 'loop'
$mk = cmd.exe /c "mklink /J `"$loop`" `"$junctionDir`"" 2>&1
if ($LASTEXITCODE -ne 0) { throw "mklink failed: $mk" }

# --- images ---------------------------------------------------------------------------------
$images = New-Dir (J $root 'images')
$withImage = New-Dir (J $images 'with_image')
New-Bytes (J $withImage '10.png') (New-Png 96 128 180 60 60 '10.png')
New-Bytes (J $withImage '2.png') (New-Png 96 128 37 99 235 '2.png (cover)')
New-Text (J $withImage '1.txt') 'not an image'

$noImage = New-Dir (J $images 'no_image')
New-Text (J $noImage 'a.txt') 'a'
New-Bytes (J $noImage 'b.pdf') $pdfBytes

$nested = New-Dir (J $images 'nested_image')
$aNo = New-Dir (J $nested 'a_no_image')
New-Text (J $aNo 'x.txt') 'x'
$bHas = New-Dir (J $nested 'b_has_image')
New-Bytes (J $bHas 'pic.png') (New-Png 96 128 120 80 200 'nested')

$d6 = J $images 'depth6_image'
for ($i = 1; $i -le 6; $i++) { $d6 = J $d6 "d$i" }
New-Dir $d6 | Out-Null
New-Bytes (J $d6 'found.png') (New-Png 96 128 20 160 120 'depth 6')

$d7 = J $images 'deep_image'
for ($i = 1; $i -le 7; $i++) { $d7 = J $d7 "d$i" }
New-Dir $d7 | Out-Null
New-Bytes (J $d7 'too_deep.png') (New-Png 96 128 200 160 20 'depth 7')

$broken = New-Dir (J $images 'broken_image')
New-Bytes (J $broken 'broken.png') ([byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A) + (New-Object byte[] 40))
New-Text (J $broken 'broken.jpg') 'this is not a jpeg'

# --- executables ---------------------------------------------------------------------------
$exe = New-Dir (J $root 'executables')
New-Text (J $exe 'test.bat') "echo test`r`n"
New-Text (J $exe 'readme.txt') 'test.bat must ask for confirmation before it runs.'

# --- empty ---------------------------------------------------------------------------------
New-Dir (J $root 'empty') | Out-Null

# --- access_denied (last: icacls) ---------------------------------------------------------
$denied = New-Dir (J $root 'access_denied')
New-Text (J $denied 'secret.txt') 'you should not be able to list this folder'
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$icaclsOut = & icacls.exe $denied /deny "*${sid}:(OI)(CI)R" 2>&1
if ($LASTEXITCODE -ne 0) { throw "icacls failed: $icaclsOut" }
$revert = "icacls `"$denied`" /remove:d *$sid"

# --- summary -------------------------------------------------------------------------------
$info = @"
FolderDeck fixture
created : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
root    : $root

nest8          8 nested levels (L1_A .. L8_B), 2 folders + 3 files per folder
many_files     10000 files (file_1.txt .. file_10000.txt)
many_folders   5000 folders (folder_1 .. folder_5000)
long_path      deepest folder path length: $($longLeaf.Length) characters
names          Japanese / spaces / emoji / & / % / long name
attributes     visible.txt + hidden, system, hidden+system, .dotfile, .dotfolder, hidden_folder
junction\loop  junction -> junction (its own parent)
images         with_image (cover 2.png), no_image, nested_image, depth6_image (found),
               deep_image (depth 7, not found), broken_image
executables    test.bat (echo test)
empty          empty folder
access_denied  read denied for the current user

Revert the access_denied folder (required before deleting the fixture):
  $revert
"@
[System.IO.File]::WriteAllText((J $root '_fixture_info.txt'), $info, $utf8)

$watch.Stop()
Write-Output $info
Write-Output ("done in {0:N1} s" -f $watch.Elapsed.TotalSeconds)
