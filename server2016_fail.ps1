# You can run this script directly from an elevated powershell prompt
#
# iex ((New-Object System.Net.WebClient).DownloadString('https://raw.githubusercontent.com/careri/StarcounterShadowspanTest/master/server2016_fail.ps1'))

$projectDir = $PSScriptRoot
$appDir = Join-Path $env:TEMP "server2016Fail"

if (!$projectDir) {
    # If running in memory we want have a $PSScriptRoot
    $projectDir = $appDir
}

$binDir = Join-Path $projectDir "bin"
$shadowSpawnPath = Join-Path $binDir "ShadowSpawn.exe"
$driveLetters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray()
$robocopyPath = (Get-Command robocopy | select -First 1).Source

$rcScript = Join-Path $appDir "run.cmd"
$dataDir = Join-Path $appDir "data"
$backupDir = Join-Path $appDir "backup"
$dataFile = Join-Path $dataDir "date.dat"
$backupFile = Join-Path $backupDir "date.dat"

function GetFreeDrive() {
    $drives  = [System.IO.Directory]::GetLogicalDrives() | % { $_.Substring(0,1) }    
    $drives = $drives -join ""    

    foreach($c in $driveLetters) {
        if ($drives.IndexOf($c) -eq -1) {
            return "$c" + ":"
        }
    }
    throw "No free drive letter, cancelling mapping of drive";
}


function ShadowSpawn {
    Param(
        [Parameter(Mandatory=$True,Position=1)]
        [string]$sp_sourceDir,
        [Parameter(Mandatory=$True,Position=2)]
        [string]$sp_mappedDrive,
        [Parameter(Mandatory=$True,Position=3)]
        [string]$sp_command
    )
    $errorAction = $ErrorActionPreference
    $ErrorActionPreference = "Ignore"
    try {
        $sp_exe = gci $shadowSpawnPath
        $sp_dir = $sp_exe.Parent.FullName
        Push-Location $sp_dir
        Write-Host -ForegroundColor Green "[Shadowspawn] $sp_sourceDir => $sp_mappedDrive, Cmd: $sp_command"
        &$sp_exe /verbosity=3 $sp_sourceDir $sp_mappedDrive $sp_command
    } finally {
        $ErrorActionPreference = $errorAction
        Pop-Location
    }

}


$ErrorActionPreference = "SilentlyContinue"
$dataStream = $null

try {
    Clear
    mkdir $dataDir | Out-Null
    mkdir $backupDir | Out-Null
    mkdir $binDir | Out-Null
    $ErrorActionPreference = "Stop"

    if (!(Test-Path $shadowSpawnPath)) {
        
        Write-Host -ForegroundColor Green "Downloading shadowspawn.exe"
        (New-Object System.Net.WebClient).DownloadFile('https://github.com/careri/StarcounterShadowspanTest/raw/master/bin/ShadowSpawn.exe', $shadowSpawnPath)

        if (!(Test-Path $shadowSpawnPath)) {
            Write-Error "Failed to download: $shadowSpawnPath"
        }        
    }
    # Test shadowspawn
    $ssOutput = & $shadowSpawnPath /?

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Shadowspawn doesn't run, missing VC++ Redist?"
    }

    # Set the size of the stream
    $mb = 1024 * 1024
    $size = 64 # mb
    $envSize = $env:SERVER_FAIL_SIZE


    if ($envSize) {
        $size = [System.Int32]::Parse($envSize)
        Write-Host "Using $size, from environment var SERVER_FAIL_SIZE"
    } else {
        Write-Host "Using default size: $size, use environment var SERVER_FAIL_SIZE to set another size in megabyte"
    }
    $size = $size * $mb

    $dataFile = [System.IO.FileInfo]::new($dataFile)
    $dataStream = $dataFile.Open([System.IO.FileMode]::Create, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::Read)
    $dataStream.SetLength($size)



    for ($i = 0; $i -le 100; $i++) {
        $time = Get-Date
        Write-Host -ForegroundColor Green "$i, Time = $time"
        $bytes = [System.BitConverter]::GetBytes($time.Ticks)
        $dataStream.Position = 0;
        $dataStream.Write($bytes, 0, $bytes.length)
        $dataStream.Flush()

        # Shadowspawn backup
        $drive = GetFreeDrive
        $rcCMd = "$robocopyPath $drive\ $backupDir * /MIR /R:1 /W:1"
        Set-Content $rcScript -Value $rcCMd
        ShadowSpawn $dataDir $drive $rcScript

        # Read the backup
        $backupBytes = [System.IO.File]::ReadAllBytes($backupFile)
        $ok = [System.Linq.Enumerable]::SequenceEqual($bytes, $backupBytes)
        
        if (!$ok) {
            Write-Error "Backup failed"
        }
        Write-Host "Sleeping 2 secs"
        Start-Sleep -Seconds 2
    }
    
} catch {
    $ex = $_.Exception
    
    while ($ex) {
        Write-Host -ForegroundColor Red "$ex"

        $ex = $ex.InnerException
    }
}
finally
{
    $ErrorActionPreference = "SilentlyContinue"

    if ($dataStream) {
       $dataStream.Dispose()
    }
    rm $dataDir -Recurse | Out-Null
    rm $backupDir -Recurse | Out-Null
}