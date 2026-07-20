Add-Type -AssemblyName System.Drawing
$pngPath = "D:\StarCitizenJapaneseTextCreater\676b9b8484cf4af0.png"
$icoPath = "D:\StarCitizenJapaneseTextCreater\app.ico"

$img = [System.Drawing.Image]::FromFile($pngPath)
$stream = New-Object IO.FileStream($icoPath, [IO.FileMode]::Create)
$img.Save($stream, [System.Drawing.Imaging.ImageFormat]::Icon)
$stream.Close()
$img.Dispose()
