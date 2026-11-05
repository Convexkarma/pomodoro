$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $framework 'csc.exe'
if (-not (Test-Path $csc)) { throw 'The Windows .NET Framework compiler is not installed.' }

Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap 64,64
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = 'AntiAlias'
$graphics.Clear([System.Drawing.Color]::FromArgb(33,54,74))
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(163,216,200)),5
$graphics.DrawArc($pen,13,13,38,38,-90,285)
$graphics.DrawLine($pen,32,20,32,33)
$graphics.DrawLine($pen,32,33,41,38)
$icon = [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())
$stream = [System.IO.File]::Create((Join-Path $root 'QuietFocus.ico'))
$icon.Save($stream)
$stream.Dispose(); $graphics.Dispose(); $pen.Dispose(); $bitmap.Dispose()

$refs = @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll') | ForEach-Object { '/r:' + (Join-Path $framework $_) }
$refs += @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll') | ForEach-Object { '/r:' + (Join-Path "$framework\WPF" $_) }
$arguments = @('/nologo','/target:winexe','/platform:anycpu','/optimize+',('/out:' + (Join-Path $root 'QuietFocus.exe')),('/win32icon:' + (Join-Path $root 'QuietFocus.ico')),('/resource:' + (Join-Path $root 'source\MainWindow.xaml') + ',QuietFocus.MainWindow.xaml')) + $refs + @((Join-Path $root 'source\TimerEngine.cs'),(Join-Path $root 'source\App.cs'))
& $csc $arguments
if ($LASTEXITCODE -ne 0) { throw 'App compilation failed.' }
$testArguments = @('/nologo','/target:exe',('/out:' + (Join-Path $root 'CoreTests.exe'))) + $refs + @((Join-Path $root 'source\TimerEngine.cs'),(Join-Path $root 'source\Tests.cs'))
& $csc $testArguments
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& (Join-Path $root 'CoreTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
Write-Output 'BUILD_AND_CORE_TESTS_PASSED'
